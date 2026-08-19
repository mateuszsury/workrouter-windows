using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using WorkRouter.Abstractions;
using WorkRouter.Models;

namespace WorkRouter.Monitoring;

public sealed class NetworkInterfaceUsageMonitor : IUsageMonitor
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ClientState> _clients = new(StringComparer.OrdinalIgnoreCase);
    private NetworkInterface? _networkInterface;
    private long _lastSent;
    private long _lastReceived;
    private DateTimeOffset _lastSample;
    private long _sessionSent;
    private long _sessionReceived;
    private long _uploadRate;
    private long _downloadRate;
    private string? _primaryMac;

    public Task StartAsync(NetworkTopology topology, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var match = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(candidate =>
            {
                try
                {
                    return candidate.GetIPProperties().GetIPv4Properties()?.Index == topology.InterfaceIndex;
                }
                catch (NetworkInformationException)
                {
                    return false;
                }
            });
        if (match is null)
        {
            throw new InvalidOperationException("Nie można przypisać licznika do interfejsu hotspotu.");
        }

        lock (_sync)
        {
            _networkInterface = match;
            var statistics = match.GetIPStatistics();
            _lastSent = statistics.BytesSent;
            _lastReceived = statistics.BytesReceived;
            _lastSample = DateTimeOffset.UtcNow;
            _sessionSent = 0;
            _sessionReceived = 0;
            _uploadRate = 0;
            _downloadRate = 0;
            _clients.Clear();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _networkInterface = null;
            _uploadRate = 0;
            _downloadRate = 0;
        }

        return Task.CompletedTask;
    }

    public Task UpdateClientsAsync(IReadOnlyList<HotspotClientSnapshot> clients, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var active = new HashSet<string>(clients.Select(client => NormalizeMac(client.MacAddress)), StringComparer.OrdinalIgnoreCase);
            foreach (var client in clients)
            {
                var key = NormalizeMac(client.MacAddress);
                _clients[key] = new ClientState(client.HostName, client.Address?.ToString(), DateTimeOffset.UtcNow);
            }

            foreach (var key in _clients.Keys.Where(key => !active.Contains(key)).ToArray())
            {
                _clients.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<ClientUsage> Snapshot()
    {
        lock (_sync)
        {
            SampleInterface();
            var primary = _primaryMac;
            if (string.IsNullOrEmpty(primary) || !_clients.ContainsKey(primary))
            {
                primary = _clients.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            }

            return _clients.Select(pair =>
            {
                var ownsAggregate = string.Equals(pair.Key, primary, StringComparison.OrdinalIgnoreCase);
                return new ClientUsage(
                    pair.Key,
                    pair.Value.HostName,
                    pair.Value.IpAddress,
                    ownsAggregate ? _uploadRate : 0,
                    ownsAggregate ? _downloadRate : 0,
                    ownsAggregate ? _sessionReceived : 0,
                    ownsAggregate ? _sessionSent : 0,
                    ownsAggregate ? _sessionReceived : 0,
                    ownsAggregate ? _sessionSent : 0,
                    ownsAggregate,
                    _clients.Count != 1,
                    pair.Value.LastSeen);
            }).OrderByDescending(client => client.IsPrimary).ThenBy(client => client.HostName).ToArray();
        }
    }

    public void MarkPrimary(string macAddress)
    {
        var normalized = NormalizeMac(macAddress);
        lock (_sync)
        {
            _primaryMac = normalized;
        }
    }

    public ValueTask DisposeAsync()
    {
        _networkInterface = null;
        return ValueTask.CompletedTask;
    }

    private void SampleInterface()
    {
        if (_networkInterface is null)
        {
            return;
        }

        try
        {
            var statistics = _networkInterface.GetIPStatistics();
            var now = DateTimeOffset.UtcNow;
            var elapsed = Math.Max(0.1, (now - _lastSample).TotalSeconds);
            var sentDelta = Math.Max(0, statistics.BytesSent - _lastSent);
            var receivedDelta = Math.Max(0, statistics.BytesReceived - _lastReceived);
            // From the WORK client's perspective, bytes sent by the hotspot
            // interface are downloads and bytes received are uploads.
            _downloadRate = (long)(sentDelta / elapsed);
            _uploadRate = (long)(receivedDelta / elapsed);
            _sessionSent += sentDelta;
            _sessionReceived += receivedDelta;
            _lastSent = statistics.BytesSent;
            _lastReceived = statistics.BytesReceived;
            _lastSample = now;
        }
        catch (NetworkInformationException)
        {
            _uploadRate = 0;
            _downloadRate = 0;
        }
    }

    private static string NormalizeMac(string value)
    {
        var characters = value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray();
        if (characters.Length != 12)
        {
            throw new ArgumentException("Nieprawidłowy adres MAC.", nameof(value));
        }

        return string.Join(':', Enumerable.Range(0, 6).Select(index => new string(characters, index * 2, 2)));
    }

    private sealed record ClientState(string? HostName, string? IpAddress, DateTimeOffset LastSeen);
}
