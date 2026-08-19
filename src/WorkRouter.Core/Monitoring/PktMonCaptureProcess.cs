using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using WorkRouter.Models;

namespace WorkRouter.Monitoring;

internal sealed record PktMonObservation(
    IPAddress Source,
    IPAddress Destination,
    int SourcePort,
    int DestinationPort,
    string Protocol,
    int Bytes,
    string? Domain);

/// <summary>
/// Adapter-only fallback built on Packet Monitor shipped with Windows. It reads
/// real-time decoded metadata and never creates an ETL file. Packet Monitor is
/// global, so an existing external capture is never replaced.
/// </summary>
internal sealed class PktMonCaptureProcess : IDisposable
{
    private static readonly Regex ComponentLine = new(
        @"^\s*(?<id>\d+)\s+(?<mac>(?:[0-9A-F]{2}-){5}[0-9A-F]{2})\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeaderLine = new(@"OriginalSize\s+(?<size>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Ipv4Line = new(
        @"(?<src>(?:\d{1,3}\.){3}\d{1,3})\.(?<sport>\d+)\s+>\s+(?<dst>(?:\d{1,3}\.){3}\d{1,3})\.(?<dport>\d+):\s*(?<tail>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DnsQuestion = new(
        @"\?\s+(?<domain>(?:[a-z0-9_-]+\.)+[a-z0-9_-]+)\.?\s*(?:\(|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] OwnedFilterNames = { "WorkRouterDnsUdp", "WorkRouterDnsTcp", "WorkRouterTcpSyn", "WorkRouterIcmp" };

    private readonly string _executable;
    private readonly Process _process;
    private readonly Action<PktMonObservation> _onObservation;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _stdoutReader;
    private Task? _stderrReader;
    private int _lastOriginalSize;
    private bool _ownsCapture;
    private bool _ownsFilters;
    private bool _disposed;

    private PktMonCaptureProcess(string executable, Process process, Action<PktMonObservation> onObservation)
    {
        _executable = executable;
        _process = process;
        _onObservation = onObservation;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnExited;
    }

    public Task Completion => _completion.Task;

    public static PktMonCaptureProcess Start(NetworkTopology topology, Action<PktMonObservation> onObservation)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pktmon.exe");
        if (!File.Exists(executable)) throw new PlatformNotSupportedException("Windows Packet Monitor nie jest dostępny.");
        var status = RunCommand(executable, "status");
        if (!status.Output.Contains("not running", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Packet Monitor jest już używany przez inny proces; WorkRouter go nie przejmie.");

        var componentId = FindComponentId(executable, topology);
        var filtersInstalled = false;
        Process? process = null;
        PktMonCaptureProcess? capture = null;
        try
        {
            InstallOwnedFilters(executable);
            filtersInstalled = true;
            process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            // Metadata parsing only needs the IP/TCP/UDP headers. Keeping the
            // captured prefix small materially reduces stdout/event pressure.
            // Flow events retain endpoint/protocol metadata while avoiding a
            // per-packet firehose that can consume the PktMon 1 GB real-time
            // ring under a busy hotspot.
            Arguments = $"start --capture --comp {componentId} --type flow --pkt-size 128 --flags 0x010 --file-name NUL --log-mode real-time",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Nie można uruchomić Windows Packet Monitor.");
        capture = new PktMonCaptureProcess(executable, process, onObservation);
        capture._ownsCapture = true;
        capture._ownsFilters = true;
        // Dedicated readers avoid Process.DataReceived's unbounded ThreadPool
        // callback queue during monitor stop/start cycles.
        capture._stdoutReader = capture.ReadOutputAsync(capture._readerCts.Token);
        capture._stderrReader = capture.ReadErrorAsync(capture._readerCts.Token);
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"Packet Monitor zakończył start z kodem {process.ExitCode}.");
                var liveStatus = RunCommand(executable, "status").Output;
                if (liveStatus.Contains("Real-Time", StringComparison.OrdinalIgnoreCase) &&
                    liveStatus.Contains($" {componentId} ", StringComparison.Ordinal))
                {
                    return capture;
                }
                Thread.Sleep(500);
            }
            throw new TimeoutException("Packet Monitor nie potwierdził interfejsu WORK w ciągu 10 sekund.");
        }
        catch
        {
            capture?.Dispose();
            throw;
        }
        }
        catch
        {
            capture?.Dispose();
            if (capture is null && filtersInstalled)
                TryRemoveOwnedFilters(executable, OwnedFilterNames);
            try
            {
                if (capture is null && process is not null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsCapture)
        {
            try { RunCommand(_executable, "stop"); }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
        }
        try
        {
            if (!_process.HasExited && !_process.WaitForExit(3000)) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        try
        {
            _readerCts.Cancel();
            var readers = new[] { _stdoutReader, _stderrReader }.Where(task => task is not null).Cast<Task>().ToArray();
            if (readers.Length > 0) Task.WaitAll(readers, TimeSpan.FromSeconds(2));
        }
        catch (AggregateException) { }
        catch (OperationCanceledException) { }
        _process.Dispose();
        _readerCts.Dispose();
        RemoveOwnedFiltersIfUnchanged();
        _completion.TrySetResult();
    }

    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                ProcessOutputLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void ProcessOutputLine(string line)
    {
        if (line.Contains("Processing", StringComparison.OrdinalIgnoreCase))
        {
            _ready.TrySetResult();
            return;
        }
        var header = HeaderLine.Match(line);
        if (header.Success)
        {
            int.TryParse(header.Groups["size"].Value, out _lastOriginalSize);
            return;
        }
        if (TryParsePacketLine(line, _lastOriginalSize, out var observation))
            _onObservation(observation);
    }

    private async Task ReadErrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line) && !_ready.Task.IsCompleted)
                    _ready.TrySetException(new InvalidOperationException("Packet Monitor nie rozpoczął przechwytywania."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    internal static bool TryParsePacketLine(string line, int originalSize, out PktMonObservation observation)
    {
        observation = null!;
        var packet = Ipv4Line.Match(line);
        if (!packet.Success || !IPAddress.TryParse(packet.Groups["src"].Value, out var source) ||
            !IPAddress.TryParse(packet.Groups["dst"].Value, out var destination) ||
            !int.TryParse(packet.Groups["sport"].Value, out var sourcePort) ||
            !int.TryParse(packet.Groups["dport"].Value, out var destinationPort)) return false;

        var tail = packet.Groups["tail"].Value;
        var protocol = tail.Contains("UDP", StringComparison.OrdinalIgnoreCase) || sourcePort is 53 or 5353 || destinationPort is 53 or 5353
            ? "udp"
            : tail.Contains("Flags", StringComparison.OrdinalIgnoreCase) ? "tcp" : string.Empty;
        if (protocol.Length == 0) return false;
        var question = DnsQuestion.Match(tail);
        var domain = question.Success ? question.Groups["domain"].Value.TrimEnd('.') : null;
        observation = new PktMonObservation(source, destination, sourcePort, destinationPort, protocol, Math.Max(originalSize, 20), domain);
        return true;
    }

    private void OnExited(object? sender, EventArgs args)
    {
        if (!_ready.Task.IsCompleted)
            _ready.TrySetException(new InvalidOperationException($"Packet Monitor zakończył start z kodem {_process.ExitCode}."));
        _completion.TrySetResult();
    }

    private static int FindComponentId(string executable, NetworkTopology topology)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(candidate =>
            Guid.TryParse(candidate.Id.Trim('{', '}'), out var id) && id == topology.InterfaceGuid);
        if (adapter is null) throw new InvalidOperationException("Nie można powiązać interfejsu WORK z komponentem Packet Monitor.");
        var mac = string.Join('-', adapter.GetPhysicalAddress().GetAddressBytes().Select(value => value.ToString("X2")));
        foreach (var line in RunCommand(executable, "comp list").Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ComponentLine.Match(line);
            if (match.Success && string.Equals(match.Groups["mac"].Value, mac, StringComparison.OrdinalIgnoreCase))
                return int.Parse(match.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        throw new InvalidOperationException($"Packet Monitor nie widzi adaptera WORK ({adapter.Description}).");
    }

    private static void InstallOwnedFilters(string executable)
    {
        var existing = RunCommand(executable, "filter list").Output;
        if (HasAnyFilters(existing))
        {
            // A previous crash can leave exactly our named filters behind. They
            // are safe to reclaim; any unknown/foreign row is preserved and
            // causes a fail-closed monitor start instead.
            if (!ContainsOnlyNamedFilters(existing, OwnedFilterNames))
                throw new InvalidOperationException("Packet Monitor ma już globalne filtry; WorkRouter nie zmieni filtrów należących do innego procesu.");
            var cleanup = RunCommand(executable, "filter remove");
            if (cleanup.ExitCode != 0 || HasAnyFilters(RunCommand(executable, "filter list").Output))
                throw new InvalidOperationException("Nie można bezpiecznie usunąć osieroconych filtrów WorkRouter.");
        }

        var installed = new List<string>();
        try
        {
            foreach (var arguments in new[]
            {
                "filter add WorkRouterDnsUdp -d IPv4 -t UDP -p 53",
                "filter add WorkRouterDnsTcp -d IPv4 -t TCP -p 53",
                "filter add WorkRouterTcpSyn -d IPv4 -t TCP SYN",
                "filter add WorkRouterIcmp -d IPv4 -t ICMP"
            })
            {
                var result = RunCommand(executable, arguments);
                if (result.ExitCode != 0 || result.Output.Contains("error", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Nie można założyć filtra PktMon ({arguments}).");
                installed.Add(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
            }
        }
        catch
        {
            // At this point we proved the list was empty. Remove only when the
            // resulting list still contains exclusively our names; if another
            // process raced us, preserve its filters and fail closed.
            TryRemoveOwnedFilters(executable, installed);
            throw;
        }
    }

    private void RemoveOwnedFiltersIfUnchanged()
    {
        if (!_ownsFilters) return;
        TryRemoveOwnedFilters(_executable, OwnedFilterNames);
        _ownsFilters = false;
    }

    private static void TryRemoveOwnedFilters(string executable, IReadOnlyCollection<string> expectedNames)
    {
        try
        {
            var current = RunCommand(executable, "filter list").Output;
            if (!ContainsOnlyNamedFilters(current, expectedNames)) return;
            _ = RunCommand(executable, "filter remove");
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
    }

    private static bool HasAnyFilters(string output)
    {
        var marker = output.IndexOf("Packet Filters:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return true;
        var lines = output[marker..].Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Skip(1).Any(line =>
        {
            var value = line.Trim();
            return value.Length > 0 && !value.Equals("None", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool ContainsOnlyNamedFilters(string output, IReadOnlyCollection<string> expectedNames)
    {
        if (expectedNames.Count == 0) return false;
        var marker = output.IndexOf("Packet Filters:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        var lines = output[marker..].Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.Equals("None", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("#", StringComparison.Ordinal)
                && !line.StartsWith("-", StringComparison.Ordinal))
            .ToArray();
        // Header rows are not filter names; a real row contains one of the
        // names as a token. Unknown rows make cleanup unsafe.
        var names = lines.Where(line => expectedNames.Any(name => line.Contains(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        return names.Length == lines.Length && names.Length == expectedNames.Count;
    }

    private static (int ExitCode, string Output) RunCommand(string executable, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        }) ?? throw new InvalidOperationException($"Nie można uruchomić {Path.GetFileName(executable)}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(executable)} nie zakończył polecenia {arguments}.");
        }
        return (process.ExitCode, output + error);
    }
}
