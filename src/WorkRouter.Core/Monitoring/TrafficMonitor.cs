using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.RegularExpressions;
using WorkRouter.Abstractions;
using WorkRouter.Core.Networking;
using WorkRouter.Models;

namespace WorkRouter.Monitoring;

/// <summary>
/// Best-effort, metadata-only capture. SIO_RCVALL requires elevation and is not a
/// guaranteed view of packets forwarded by Internet Connection Sharing; failures are
/// reported as unsupported instead of being represented as a healthy monitor.
/// </summary>
public sealed class RawSocketTrafficMonitor : ITrafficMonitor
{
    private const int MaxEvents = 4096;
    private readonly object _gate = new();
    private readonly Queue<TrafficEvent> _events = new();
    private readonly List<TrafficAlert> _alerts = new();
    private readonly Dictionary<string, TrafficAggregateMutable> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrafficAggregateMutable> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrafficAggregateMutable> _destinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrafficAggregateMutable> _protocols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _clientDestinations = new(StringComparer.OrdinalIgnoreCase);
    private NetworkTopology? _topology;
    private TrafficPreferences _preferences = new();
    private Socket? _socket;
    private PktMonCaptureProcess? _pktMon;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private long _nextId;
    private int _encryptedUnknown;
    private int _dohLike;
    private int _dot;
    private int _quic;
    private int _vpnLike;
    private static readonly string[] Limitations = { "best-effort; podstawowym źródłem nazw jest jawny DNS", "DoH/ECH szyfrują nazwę domeny", "QUIC/UDP 443, DoT i VPN ograniczają widoczność", "brak MITM, zapisu ETL i treści/payloadów" };
    private TrafficCaptureStatus _status = new(false, false, true, null, null, "Inspekcja nieaktywna.", Limitations);
    private static readonly Regex DomainRegex = new(@"^[a-z0-9][a-z0-9.-]{0,252}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public TrafficCaptureStatus Status { get { lock (_gate) return _status; } }

    public async Task StartAsync(NetworkTopology topology, TrafficPreferences preferences, CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _topology = topology;
            _preferences = preferences.Normalize();
            if (!_preferences.TrafficInspectionEnabled)
            {
                _status = new(false, false, true, topology.InterfaceIndex, topology.InterfaceAlias, "Inspekcja wyłączona w preferencjach.", Limitations);
                return;
            }
            var stage = "create";
            try
            {
                var address = topology.GatewayAddress;
                var usePktMon = false;
                try
                {
                    _socket = CreateBoundSocket(address);
                }
                catch (SocketException exception) when (
                    exception.SocketErrorCode == SocketError.AddressNotAvailable &&
                    OperatingSystem.IsWindows() &&
                    WindowsIdentity.GetCurrent().IsSystem)
                {
                    stage = "pktmon";
                    _socket?.Dispose();
                    _socket = null;
                    _pktMon = PktMonCaptureProcess.Start(topology, IngestObservation);
                    usePktMon = true;
                }
                stage = "capture-loop";
                _captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _captureTask = usePktMon
                    ? _pktMon!.Completion
                    : Task.Run(() => CaptureLoopAsync(_captureCts.Token), _captureCts.Token);
                var detail = usePktMon
                    ? "Windows Packet Monitor przechwytuje metadane przepływów wyłącznie na interfejsie WORK (bez pełnych pakietów)."
                    : "Raw socket SIO_RCVALL aktywny (best-effort).";
                _status = new(true, true, true, topology.InterfaceIndex, topology.InterfaceAlias, detail, Limitations);
            }
            catch (Exception ex) when (ex is SocketException or Win32Exception or UnauthorizedAccessException or InvalidOperationException or IOException or TimeoutException)
            {
                _socket?.Dispose(); _socket = null; _pktMon?.Dispose(); _pktMon = null;
                _status = new(false, false, true, topology.InterfaceIndex, topology.InterfaceAlias, $"Raw socket niedostępny ({stage}): {ex.Message}", Limitations);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate) { cts = _captureCts; task = _captureTask; _captureCts = null; _captureTask = null; _socket?.Dispose(); _socket = null; _pktMon?.Dispose(); _pktMon = null; if (_topology is not null) _status = new(_status.Supported, false, true, _topology.InterfaceIndex, _topology.InterfaceAlias, "Inspekcja zatrzymana.", Limitations); }
        cts?.Cancel();
        if (task is not null) { try { await task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { } catch (TimeoutException) { } }
        cts?.Dispose();
    }

    public void UpdatePreferences(TrafficPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate) _preferences = preferences.Normalize();
    }

    public void UpdateClients(IReadOnlyList<HotspotClientSnapshot> clients)
    {
        // The raw socket has no reliable MAC attribution; client IP is therefore the
        // stable local identifier and is refreshed by the coordinator for future adapters.
    }

    /// <summary>Injects one captured IP packet for deterministic tests and platform adapters.</summary>
    public void IngestPacket(NetworkTopology topology, ReadOnlySpan<byte> packet)
    {
        _topology = topology;
        Process(packet);
    }

    public TrafficSummary GetSummary(int windowMinutes = 60)
    {
        lock (_gate)
        {
            var to = DateTimeOffset.UtcNow; var from = to.AddMinutes(-Math.Clamp(windowMinutes, 1, 24 * 7));
            var alerts = _alerts.Where(a => a.Timestamp >= from).TakeLast(256).ToArray();
            var windowEvents = _events.Where(e => e.Timestamp >= from).ToArray();
            var windowEncrypted = windowEvents.LongCount(e => e.Note?.Contains("encrypted", StringComparison.OrdinalIgnoreCase) == true || e.VisibilitySource == "ip-only");
            var windowDoh = windowEvents.LongCount(e => e.Note?.StartsWith("DoH-like", StringComparison.OrdinalIgnoreCase) == true);
            var windowDot = windowEvents.LongCount(e => e.Note?.StartsWith("DoT", StringComparison.OrdinalIgnoreCase) == true);
            var windowQuic = windowEvents.LongCount(e => e.Note?.StartsWith("QUIC", StringComparison.OrdinalIgnoreCase) == true);
            var windowVpn = windowEvents.LongCount(e => e.Note?.StartsWith("VPN-like", StringComparison.OrdinalIgnoreCase) == true);
            return new TrafficSummary(from, to, _preferences.TrafficInspectionEnabled, _status.Running, true, false, _status.InterfaceAlias,
                SnapshotEvents(windowEvents, e => e.Client), SnapshotEvents(windowEvents, e => e.Domain), SnapshotEvents(windowEvents, e => e.Destination), SnapshotEvents(windowEvents, e => e.Protocol), windowEvents.TakeLast(256).ToArray(), alerts, Limitations,
                windowEncrypted, windowDoh, windowDot, windowQuic, windowVpn);
        }
    }

    public IReadOnlyList<TrafficEvent> GetEvents(long afterId = 0)
    { lock (_gate) return _events.Where(e => e.Id > afterId).ToArray(); }

    public void Clear()
    { lock (_gate) { _events.Clear(); _alerts.Clear(); _clients.Clear(); _domains.Clear(); _destinations.Clear(); _protocols.Clear(); _dns.Clear(); _clientDestinations.Clear(); _encryptedUnknown = _dohLike = _dot = _quic = _vpnLike = 0; } }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private static Socket CreateBoundSocket(IPAddress address)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
        try
        {
            socket.Bind(new IPEndPoint(address, 0));
            socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), null);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task CaptureLoopAsync(CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (!token.IsCancellationRequested)
        {
            try
            {
                Socket? socket; lock (_gate) socket = _socket;
                if (socket is null) break;
                var read = await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
                if (read > 0) Process(buffer.AsSpan(0, read));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (SocketException) { break; }
        }
    }

    private void Process(ReadOnlySpan<byte> packet)
    {
        if (!PacketParser.TryParseIpv4(packet, false, out var parsed) || _topology is null) return;
        if (!_topology.WorkNetwork.Contains(parsed.Source) && !_topology.WorkNetwork.Contains(parsed.Destination)) return;
        var outbound = _topology.WorkNetwork.Contains(parsed.Source);
        var clientIp = outbound ? parsed.Source : parsed.Destination;
        if (clientIp.Equals(_topology.GatewayAddress)) return;
        var remoteIp = outbound ? parsed.Destination : parsed.Source;
        var remotePort = outbound ? parsed.DestinationPort : parsed.SourcePort;
        var direction = outbound ? "outbound" : "inbound";
        string? domain = null; string? host = null; string? sni = null; string? note = null; var visibility = "ip-only"; var confidence = 0d;
        if (parsed.Protocol == "udp" && (parsed.SourcePort == 53 || parsed.DestinationPort == 53) && PacketParser.TryParseDns(parsed.Payload, parsed.Source, parsed.Destination, parsed.SourcePort, parsed.DestinationPort, out var query, out var answers))
        {
            domain = query; visibility = "dns"; confidence = .99;
            lock (_gate)
            {
                foreach (var a in answers) _dns[a.Value] = a.Name;
                while (_dns.Count > 4096) _dns.Remove(_dns.Keys.First());
            }
        }
        if (parsed.Protocol == "tcp")
        {
            host = PacketParser.TryGetHttpHost(parsed.Payload); if (host is not null) { domain = host; visibility = "http-host"; confidence = .95; }
            sni = PacketParser.TryGetTlsSni(parsed.Payload); if (sni is not null) { domain = sni; visibility = "tls-sni"; confidence = .9; }
        }
        string? correlated = null; lock (_gate) _dns.TryGetValue(remoteIp.ToString(), out correlated);
        if (domain is null && correlated is not null) { domain = correlated; visibility = "dns-correlation"; confidence = .7; }
        lock (_gate)
        {
            if (remotePort == 443 && parsed.Protocol == "udp") { note = "QUIC visibility limited"; _quic++; }
            if (remotePort == 853 || (outbound ? parsed.SourcePort : parsed.DestinationPort) == 853) { note = "DoT visibility limited"; _dot++; }
            if (remotePort == 443 && parsed.Protocol == "tcp" && sni is null) { note = "ECH/encrypted visibility limited"; _encryptedUnknown++; }
            if (remotePort is 1194 or WireGuardPort) { note = "VPN-like traffic; visibility limited"; _vpnLike++; }
            if (parsed.Protocol == "tcp" && remotePort == 443 && parsed.Payload.AsSpan().IndexOf("/dns-query"u8) >= 0) { note = "DoH-like endpoint; visibility limited"; _dohLike++; }
            if (domain is null && note is null) _encryptedUnknown++;
        }
        var client = clientIp.ToString(); var now = DateTimeOffset.UtcNow; var destination = $"{remoteIp}:{remotePort}/{parsed.Protocol}"; var bytes = parsed.PayloadLength;
        lock (_gate)
        {
            Add(_clients, client, bytes, now); Add(_destinations, destination, bytes, now); Add(_protocols, parsed.Protocol, bytes, now); if (domain is not null && DomainRegex.IsMatch(domain)) Add(_domains, domain, bytes, now);
            if (!_clientDestinations.TryGetValue(client, out var set))
            {
                _clientDestinations[client] = set = new(StringComparer.OrdinalIgnoreCase);
            }
            set.Add(destination);
            while (set.Count > 512) set.Remove(set.First());
            var id = ++_nextId; var e = new TrafficEvent(id, now, client, client, direction, parsed.Protocol, $"{parsed.Source}:{parsed.SourcePort}", destination, parsed.SourcePort, parsed.DestinationPort, bytes, domain, host, sni, note, visibility, confidence);
            _events.Enqueue(e); while (_events.Count > MaxEvents) _events.Dequeue();
            if (set.Count == 100) AddAlert(new TrafficAlert(id, now, "info", "many-destinations", client, null, "Klient osiągnął 100 różnych destynacji."));
            if (remotePort is not (53 or 80 or 123 or 443 or 445) && remotePort > 1024)
                AddAlert(new TrafficAlert(id, now, "info", "unusual-port", client, destination, $"Nietypowy port docelowy {remotePort}."));
            if (domain is not null && IsTracker(domain)) AddAlert(new TrafficAlert(id, now, "info", "tracker", client, domain, "Domena pasuje do lokalnej heurystyki trackerów; nie jest to potwierdzenie malware."));
            Prune(now);
        }
    }

    private void IngestObservation(PktMonObservation observation)
    {
        // StartAsync launches pktmon while holding the lifecycle gate. Do not let
        // stdout callbacks queue unboundedly behind that gate: metadata capture is
        // best-effort and dropping a line is safer than retaining gigabytes of
        // backlogged packet events under sustained traffic.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            IngestObservationCore(observation);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private void IngestObservationCore(PktMonObservation observation)
    {
        var topology = _topology;
        if (topology is null || (!topology.WorkNetwork.Contains(observation.Source) && !topology.WorkNetwork.Contains(observation.Destination))) return;
        var outbound = topology.WorkNetwork.Contains(observation.Source);
        var clientIp = outbound ? observation.Source : observation.Destination;
        if (clientIp.Equals(topology.GatewayAddress)) return;
        var remoteIp = outbound ? observation.Destination : observation.Source;
        var remotePort = outbound ? observation.DestinationPort : observation.SourcePort;
        var client = clientIp.ToString();
        var domain = observation.Domain is not null && DomainRegex.IsMatch(observation.Domain) ? observation.Domain : null;
        var visibility = domain is null ? "ip-only" : "dns";
        var confidence = domain is null ? 0d : .96;
        string? note = null;
        lock (_gate)
        {
            if (remotePort == 443 && observation.Protocol == "udp") { note = "QUIC visibility limited"; _quic++; }
            if (remotePort == 853 || observation.SourcePort == 853 || observation.DestinationPort == 853) { note = "DoT visibility limited"; _dot++; }
            if (remotePort == 443 && observation.Protocol == "tcp") { note = "ECH/encrypted visibility limited"; _encryptedUnknown++; }
            if (remotePort is 1194 or WireGuardPort) { note = "VPN-like traffic; visibility limited"; _vpnLike++; }
            if (domain is null && note is null) _encryptedUnknown++;

            var now = DateTimeOffset.UtcNow;
            var destination = $"{remoteIp}:{remotePort}/{observation.Protocol}";
            Add(_clients, client, observation.Bytes, now);
            Add(_destinations, destination, observation.Bytes, now);
            Add(_protocols, observation.Protocol, observation.Bytes, now);
            if (domain is not null) Add(_domains, domain, observation.Bytes, now);
            if (!_clientDestinations.TryGetValue(client, out var set)) _clientDestinations[client] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(destination);
            while (set.Count > 512) set.Remove(set.First());
            var id = ++_nextId;
            _events.Enqueue(new TrafficEvent(id, now, client, client, outbound ? "outbound" : "inbound", observation.Protocol,
                $"{observation.Source}:{observation.SourcePort}", destination, observation.SourcePort, observation.DestinationPort,
                observation.Bytes, domain, null, null, note, visibility, confidence));
            while (_events.Count > MaxEvents) _events.Dequeue();
            if (set.Count == 100) AddAlert(new TrafficAlert(id, now, "info", "many-destinations", client, null, "Klient osiągnął 100 różnych destynacji."));
            if (remotePort is not (53 or 80 or 123 or 443 or 445 or 5353) && remotePort > 1024)
                AddAlert(new TrafficAlert(id, now, "info", "unusual-port", client, destination, $"Nietypowy port docelowy {remotePort}."));
            if (domain is not null && IsTracker(domain))
                AddAlert(new TrafficAlert(id, now, "info", "tracker", client, domain, "Domena pasuje do lokalnej heurystyki trackerów; nie jest to potwierdzenie malware."));
            Prune(now);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddHours(-_preferences.RetentionHours);
        while (_events.Count > 0 && _events.Peek().Timestamp < cutoff) _events.Dequeue();
        while (_alerts.Count > 0 && _alerts[0].Timestamp < cutoff) _alerts.RemoveAt(0);
        foreach (var dict in new[] { _clients, _domains, _destinations, _protocols })
        {
            while (dict.Count > 1024) dict.Remove(dict.OrderBy(x => x.Value.LastSeen).First().Key);
        }
    }

    private void AddAlert(TrafficAlert alert)
    {
        if (_alerts.Count >= 256 || _alerts.Any(a => a.Kind == alert.Kind && a.Client == alert.Client && a.Destination == alert.Destination && alert.Timestamp - a.Timestamp < TimeSpan.FromMinutes(1))) return;
        _alerts.Add(alert);
    }

    private static void Add(Dictionary<string, TrafficAggregateMutable> dict, string key, long bytes, DateTimeOffset now)
    {
        if (!dict.TryGetValue(key, out var value)) dict[key] = value = new();
        value.Bytes += bytes;
        value.Packets++;
        value.LastSeen = now;
    }
    private static TrafficAggregate[] SnapshotEvents(IEnumerable<TrafficEvent> events, Func<TrafficEvent, string?> keySelector) => events.Where(e => !string.IsNullOrWhiteSpace(keySelector(e))).GroupBy(e => keySelector(e)!, StringComparer.OrdinalIgnoreCase).Select(g => new TrafficAggregate(g.Key, g.Sum(e => e.Bytes), g.LongCount(), g.Max(e => e.Timestamp))).OrderByDescending(v => v.Bytes).Take(256).ToArray();
    private static bool IsTracker(string domain) => domain.Contains("doubleclick.", StringComparison.OrdinalIgnoreCase) || domain.Contains("googlesyndication.", StringComparison.OrdinalIgnoreCase) || domain.Contains("adservice.", StringComparison.OrdinalIgnoreCase) || domain.Contains("analytics.", StringComparison.OrdinalIgnoreCase);
    private const int WireGuardPort = 51820;
    private sealed class TrafficAggregateMutable { public long Bytes; public long Packets; public DateTimeOffset LastSeen; }
}
