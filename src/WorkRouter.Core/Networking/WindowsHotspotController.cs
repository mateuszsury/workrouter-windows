using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using WorkRouter.Abstractions;
using WorkRouter.Models;
using RouterHotspotSnapshot = WorkRouter.Models.HotspotSnapshot;

namespace WorkRouter.Core.Networking;

/// <summary>
/// Native Windows 10/11 Mobile Hotspot controller. The controller deliberately
/// refuses to start when an Ethernet connection cannot be identified; falling
/// back to the user's Wi-Fi would bridge the wrong network.
/// </summary>
public sealed class WindowsHotspotController : WorkRouter.Abstractions.IHotspotController
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NetworkOperatorTetheringManager? _manager;
    private string? _upstreamInterface;
    private bool _disposed;

    public async Task<RouterHotspotSnapshot> InspectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = await GetCandidatePrivateInterfaceIndexesAsync(cancellationToken).ConfigureAwait(false);
            var profile = FindEthernetProfile("Ethernet");
            var manager = _manager ??= CreateManager(profile);
            var clients = ReadClients(manager);
            var running = manager.TetheringOperationalState == TetheringOperationalState.On;
            var capabilityValue = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
            var capability = capabilityValue.ToString();
            var topology = running ? BuildTopology(candidates) : null;
            var activeBand = running ? ReadActiveBand(manager) : null;
            return new RouterHotspotSnapshot(
                IsSupported: capabilityValue == TetheringCapability.Enabled,
                IsRunning: running,
                ClientCount: clients.Count,
                MaxClientCount: checked((int)manager.MaxClientCount),
                Capability: capability,
                Failure: null,
                Topology: topology,
                Clients: clients,
                ActiveBand: activeBand);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return new RouterHotspotSnapshot(false, false, 0, 0, "Unavailable", ex.Message, null, Array.Empty<HotspotClientSnapshot>());
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<int>> GetCandidatePrivateInterfaceIndexesAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<int>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            var description = adapter.Description ?? string.Empty;
            var name = adapter.Name ?? string.Empty;
            var looksLikeTethering = name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase) ||
                                     description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                                     description.Contains("Hosted Network", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeTethering)
                continue;

            try
            {
                var index = adapter.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
                if (index > 0)
                    result.Add(index);
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear while ICS recreates it. It is not a reason to guess an index.
            }
        }
        return Task.FromResult<IReadOnlyList<int>>(result.Distinct().ToArray());
    }

    public async Task<RouterHotspotSnapshot> StartAsync(RouterSettings settings, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = FindEthernetProfile(settings.UpstreamInterface);
            var manager = CreateManager(profile);
            _manager = manager;
            var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
            if (capability != TetheringCapability.Enabled)
                return Failure("tethering_not_supported", capability.ToString());

            var requestedBand = ParseBand(settings.Band);
            var bandProbe = new NetworkOperatorTetheringAccessPointConfiguration();
            if (requestedBand != TetheringWiFiBand.Auto && !bandProbe.IsBandSupported(requestedBand))
                return Failure("band_not_supported", $"Pasmo {settings.Band} nie jest obsługiwane przez adapter Wi-Fi.");
            var configuration = new NetworkOperatorTetheringAccessPointConfiguration
            {
                Ssid = ValidateSsid(settings.Ssid),
                Passphrase = ValidatePassphrase(settings.Passphrase),
                Band = requestedBand,
            };
            await manager.ConfigureAccessPointAsync(configuration).AsTask(cancellationToken).ConfigureAwait(false);

            // ConfigureAccessPointAsync can restore the Windows five-minute no-client
            // timeout from the persisted Mobile Hotspot configuration. Disable it only
            // after provisioning, then repeat the operation after starting the session.
            // The read-back makes this fail closed instead of reporting a router that
            // Windows will silently turn off five minutes later.
            DisableAndVerifyNoConnectionsTimeout();

            if (manager.TetheringOperationalState != TetheringOperationalState.On)
            {
                var operation = await manager.StartTetheringAsync().AsTask(cancellationToken).ConfigureAwait(false);
                if (operation.Status != TetheringOperationStatus.Success)
                    return Failure("tethering_start_failed", operation.Status.ToString());
            }
            DisableAndVerifyNoConnectionsTimeout();

            _upstreamInterface = profile.NetworkAdapter?.NetworkAdapterId.ToString("D");
            var candidates = await GetCandidatePrivateInterfaceIndexesAsync(cancellationToken).ConfigureAwait(false);
            var topology = await WaitForTopologyAsync(candidates, cancellationToken).ConfigureAwait(false);
            if (topology is null)
            {
                // Never report a running hotspot without an address/interface to which policy can bind.
                await manager.StopTetheringAsync().AsTask(cancellationToken).ConfigureAwait(false);
                _manager = null;
                return Failure("hotspot_interface_not_found", "Windows did not expose the hotspot interface.");
            }

            var clients = ReadClients(manager);
            return new RouterHotspotSnapshot(true, true, clients.Count, checked((int)manager.MaxClientCount), capability.ToString(), null, topology, clients, ReadActiveBand(manager));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NetworkInformationException)
        {
            if (_manager is not null && _manager.TetheringOperationalState == TetheringOperationalState.On)
            {
                try { await _manager.StopTetheringAsync().AsTask(CancellationToken.None).ConfigureAwait(false); }
                catch { /* quarantine remains installed; preserve the original failure */ }
            }
            _manager = null;
            return Failure("tethering_start_failed", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_manager is not null && _manager.TetheringOperationalState == TetheringOperationalState.On)
                await _manager.StopTetheringAsync().AsTask(cancellationToken).ConfigureAwait(false);
            _manager = null;
            _upstreamInterface = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* disposal must not hide a prior failure */ }
        _disposed = true;
        _gate.Dispose();
    }

    private NetworkOperatorTetheringManager CreateManager(ConnectionProfile profile)
    {
        if (profile.NetworkAdapter is null)
            throw new InvalidOperationException("The selected upstream connection has no network adapter.");
        _upstreamInterface = profile.NetworkAdapter.NetworkAdapterId.ToString("D");
        return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
    }

    private static void DisableAndVerifyNoConnectionsTimeout()
    {
        NetworkOperatorTetheringManager.DisableNoConnectionsTimeout();
        if (NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled())
            throw new InvalidOperationException("Windows did not disable the Mobile Hotspot no-connections timeout.");
    }

    private static ConnectionProfile FindEthernetProfile(string? requested)
    {
        var profiles = NetworkInformation.GetConnectionProfiles()
            .Where(p => p.NetworkAdapter is not null && !p.IsWlanConnectionProfile && !p.IsWwanConnectionProfile)
            .Where(p => p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess)
            .ToArray();
        var profile = profiles.FirstOrDefault(p =>
            string.Equals(requested, p.ProfileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requested, p.NetworkAdapter?.NetworkAdapterId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requested, "Ethernet", StringComparison.OrdinalIgnoreCase) && p.NetworkAdapter?.IanaInterfaceType == 6);
        return profile ?? throw new InvalidOperationException("No connected Ethernet upstream was found.");
    }

    private async Task<NetworkTopology?> WaitForTopologyAsync(IReadOnlyList<int> candidates, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 20; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var topology = BuildTopology(candidates);
            if (topology is not null)
                return topology;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            candidates = await GetCandidatePrivateInterfaceIndexesAsync(cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static NetworkTopology? BuildTopology(IReadOnlyList<int> candidateIndexes)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;
                var props = adapter.GetIPProperties();
                var index = props.GetIPv4Properties()?.Index ?? 0;
                if (!candidateIndexes.Contains(index))
                    continue;
                var ipv4 = props.UnicastAddresses.FirstOrDefault(a =>
                    a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IsIpv4LinkLocal(a.Address));
                if (ipv4 is null)
                    continue;
                var gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address ?? ipv4.Address;
                if (index == 0)
                    continue;
                // The parent model supplies the strongly typed IPNetwork. Using the network's
                // factory keeps this code independent of whether it is a class or record struct.
                var workNetwork = IpNetwork.FromAddress(ipv4.Address, ipv4.PrefixLength);
                var interfaceGuid = Guid.TryParse(adapter.Id, out var parsedGuid) ? parsedGuid : Guid.Empty;
                return new NetworkTopology(index, interfaceGuid, gateway, workNetwork, adapter.Name, candidateIndexes);
            }
            catch (NetworkInformationException) { }
        }
        return null;
    }

    internal static bool IsIpv4LinkLocal(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static IReadOnlyList<HotspotClientSnapshot> ReadClients(NetworkOperatorTetheringManager manager)
    {
        var clients = manager.GetTetheringClients();
        var result = new List<HotspotClientSnapshot>(clients.Count);
        foreach (var client in clients)
        {
            var host = client.HostNames?.FirstOrDefault()?.DisplayName;
            var ip = client.HostNames?.Select(h => h.DisplayName).FirstOrDefault(value => IPAddress.TryParse(value, out _));
            result.Add(new HotspotClientSnapshot(client.MacAddress, host, IPAddress.TryParse(ip, out var parsed) ? parsed : null, DateTimeOffset.UtcNow));
        }
        return result;
    }

    private static string? ReadActiveBand(NetworkOperatorTetheringManager manager)
    {
        try { return manager.GetCurrentAccessPointConfiguration()?.Band.ToString(); }
        catch (COMException) { return null; }
    }

    private static RouterHotspotSnapshot Failure(string code, string detail)
        => new(false, false, 0, 0, code, $"{code}: {detail}", null, Array.Empty<HotspotClientSnapshot>());

    private static string ValidateSsid(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32)
            throw new ArgumentOutOfRangeException(nameof(value), "SSID must contain 1-32 characters.");
        return value;
    }

    private static string ValidatePassphrase(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 8 or > 63)
            throw new ArgumentOutOfRangeException(nameof(value), "Passphrase must contain 8-63 characters.");
        return value;
    }

    private static TetheringWiFiBand ParseBand(string value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "2.4" or "2.4ghz" or "twopointfourgigahertz" => TetheringWiFiBand.TwoPointFourGigahertz,
            "5" or "5ghz" or "fivegigahertz" => TetheringWiFiBand.FiveGigahertz,
            _ => TetheringWiFiBand.Auto,
        };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsHotspotController));
    }
}
