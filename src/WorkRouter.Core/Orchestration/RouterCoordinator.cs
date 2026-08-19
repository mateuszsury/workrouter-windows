using System.Collections.Concurrent;
using System.Security.Principal;
using WorkRouter.Abstractions;
using WorkRouter.Configuration;
using WorkRouter.Models;

namespace WorkRouter.Orchestration;

public sealed class RouterCoordinator : IRouterCoordinator, IAsyncDisposable
{
    private readonly IHotspotController _hotspot;
    private readonly INetworkIsolation _isolation;
    private readonly IShareManager _share;
    private readonly IUsageMonitor _usage;
    private readonly RouterConfigurationStore _configuration;
    private readonly ITrafficMonitor? _traffic;
    private readonly TrafficPreferencesStore? _trafficPreferences;
    private readonly Func<bool> _isAdministrator;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _shareInspectionGate = new(1, 1);
    private readonly ConcurrentQueue<RouterEvent> _events = new();
    private RouterOperationalState _state = RouterOperationalState.Off;
    private NetworkTopology? _topology;
    private ShareHealth? _cachedShareHealth;
    private DateTimeOffset _shareHealthCheckedAt;
    private long _eventId;

    public RouterCoordinator(
        IHotspotController hotspot,
        INetworkIsolation isolation,
        IShareManager share,
        IUsageMonitor usage,
        RouterConfigurationStore configuration,
        Func<bool>? isAdministrator = null,
        ITrafficMonitor? traffic = null,
        TrafficPreferencesStore? trafficPreferences = null)
    {
        _hotspot = hotspot;
        _isolation = isolation;
        _share = share;
        _usage = usage;
        _configuration = configuration;
        _traffic = traffic;
        _trafficPreferences = trafficPreferences;
        _isAdministrator = isAdministrator ?? IsCurrentProcessAdministrator;
        AddEvent("info", "service_ready", "Usługa WorkRouter jest gotowa; router pozostaje wyłączony.");
    }

    public async Task<RouterStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
        var hotspot = await SafeInspectHotspotAsync(cancellationToken).ConfigureAwait(false);
        var isolation = await SafeInspectIsolationAsync(hotspot.Topology ?? _topology, cancellationToken).ConfigureAwait(false);
        var share = await SafeInspectShareAsync(settings, cancellationToken).ConfigureAwait(false);
        if (hotspot.IsRunning && hotspot.Topology is not null)
        {
            _topology = hotspot.Topology;
            await _usage.UpdateClientsAsync(hotspot.Clients, cancellationToken).ConfigureAwait(false);
            _traffic?.UpdateClients(hotspot.Clients);
        }

        var clients = _usage.Snapshot();
        var effectiveState = _state;
        if (effectiveState == RouterOperationalState.Off && hotspot.IsRunning)
        {
            effectiveState = RouterOperationalState.On;
            _state = effectiveState;
        }

        var gates = BuildGates(hotspot, isolation, share);
        var gateway = hotspot.Topology?.GatewayAddress.ToString() ?? _topology?.GatewayAddress.ToString();
        if (gateway is not null)
        {
            share = share with { UncPath = $@"\\{gateway}\{settings.ShareName}" };
        }

        return new RouterStatus(
            effectiveState,
            SummaryFor(effectiveState, gates),
            DateTimeOffset.UtcNow,
            gateway,
            (hotspot.Topology ?? _topology)?.WorkNetwork.ToString(),
            settings,
            gates,
            clients,
            share,
            !_isAdministrator(),
            Environment.GetEnvironmentVariable("WORKROUTER_DEVELOPMENT") == "1",
            hotspot.ActiveBand);
    }

    public async Task<OperationResult> StartAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isAdministrator())
            {
                return OperationResult.Fail("elevation_required", "Uruchom WorkRouter jako administrator lub zainstaluj usługę.");
            }

            if (_state == RouterOperationalState.On)
            {
                return OperationResult.Ok("Router już działa.");
            }

            _state = RouterOperationalState.Starting;
            AddEvent("info", "router_starting", "Rozpoczęto bezpieczne uruchamianie routera.");
            var settings = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
            var share = await _share.EnsureAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!share.Result.Success)
            {
                throw new InvalidOperationException(share.Result.Message);
            }
            _cachedShareHealth = share.Health;
            _shareHealthCheckedAt = DateTimeOffset.UtcNow;

            if (share.GeneratedPassword is not null)
            {
                AddEvent("info", "share_password_synchronized", "Utworzono konto workshare z hasłem zsynchronizowanym z hasłem Wi-Fi.");
            }

            var candidates = await _hotspot.GetCandidatePrivateInterfaceIndexesAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("Nie znaleziono adaptera Wi-Fi Direct do objęcia kwarantanną.");
            }

            await _isolation.EnterQuarantineAsync(candidates, cancellationToken).ConfigureAwait(false);
            var hotspot = await _hotspot.StartAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!hotspot.IsRunning || hotspot.Topology is null)
            {
                throw new InvalidOperationException(hotspot.Failure ?? "Hotspot nie przeszedł do stanu aktywnego.");
            }

            _topology = hotspot.Topology;
            await _isolation.ActivateAsync(_topology, cancellationToken).ConfigureAwait(false);
            var isolation = await _isolation.InspectAsync(_topology, cancellationToken).ConfigureAwait(false);
            if (!isolation.Active || !isolation.FiltersPresent || !isolation.Ipv4Protected || !isolation.Ipv6Protected)
            {
                throw new InvalidOperationException("Nie potwierdzono kompletnej polityki WFP.");
            }

            await _usage.StartAsync(_topology, cancellationToken).ConfigureAwait(false);
            await _usage.UpdateClientsAsync(hotspot.Clients, cancellationToken).ConfigureAwait(false);
            _traffic?.UpdateClients(hotspot.Clients);
            if (_traffic is not null && _trafficPreferences is not null)
            {
                var trafficPreferences = await _trafficPreferences.LoadAsync(cancellationToken).ConfigureAwait(false);
                await _traffic.StartAsync(_topology, trafficPreferences, cancellationToken).ConfigureAwait(false);
            }
            _state = RouterOperationalState.On;
            AddEvent("success", "router_started", $"{settings.Ssid} działa na {_topology.GatewayAddress}; izolacja WFP jest aktywna.");
            return OperationResult.Ok("Router został uruchomiony z aktywną izolacją.");
        }
        catch (OperationCanceledException)
        {
            await EmergencyStopAsync(CancellationToken.None).ConfigureAwait(false);
            _state = RouterOperationalState.Faulted;
            AddEvent("warning", "router_start_cancelled", "Uruchamianie anulowano; hotspot został zatrzymany, a kwarantanna pozostaje aktywna.");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await EmergencyStopAsync(CancellationToken.None).ConfigureAwait(false);
            _state = RouterOperationalState.Faulted;
            AddEvent("error", "router_start_failed", exception.Message);
            return OperationResult.Fail("router_start_failed", exception.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<OperationResult> StopAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state = RouterOperationalState.Stopping;
            AddEvent("info", "router_stopping", "Zatrzymywanie hotspotu przed usunięciem filtrów.");
            await _hotspot.StopAsync(cancellationToken).ConfigureAwait(false);
            await _usage.StopAsync(cancellationToken).ConfigureAwait(false);
            if (_traffic is not null) await _traffic.StopAsync(cancellationToken).ConfigureAwait(false);
            await _isolation.RemoveAsync(cancellationToken).ConfigureAwait(false);
            _topology = null;
            _state = RouterOperationalState.Off;
            AddEvent("success", "router_stopped", "Router został bezpiecznie zatrzymany.");
            return OperationResult.Ok("Router został zatrzymany.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _state = RouterOperationalState.Faulted;
            AddEvent("error", "router_stop_failed", exception.Message);
            return OperationResult.Fail("router_stop_failed", "Nie usunięto filtrów, ponieważ nie potwierdzono zatrzymania hotspotu: " + exception.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<OperationResult> UpdateSettingsAsync(RouterSettings settings, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is RouterOperationalState.On or RouterOperationalState.Starting)
            {
                return OperationResult.Fail("router_active", "Zatrzymaj router przed zmianą ustawień Wi-Fi.");
            }

            var validated = RouterConfigurationStore.Validate(settings with
            {
                SharePath = @"E:\Firmowe",
                ShareName = "Firmowe",
                UpstreamInterface = "Ethernet"
            });
            // Provision the synchronized workshare credential before committing
            // the new router passphrase. A failed share update must not leave a
            // saved Wi-Fi password whose SMB credential is stale.
            var share = await _share.EnsureAsync(validated, cancellationToken).ConfigureAwait(false);
            if (!share.Result.Success)
                return OperationResult.Fail("share_password_sync_failed", share.Result.Message);
            await _configuration.SaveAsync(validated, cancellationToken).ConfigureAwait(false);
            AddEvent("info", "settings_updated", "Zapisano ustawienia hotspotu.");
            return OperationResult.Ok("Ustawienia zostały zapisane.");
        }
        catch (ArgumentException exception)
        {
            return OperationResult.Fail("invalid_settings", exception.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ShareProvisionResult> RotateSharePasswordAsync(CancellationToken cancellationToken)
    {
        var settings = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = await _share.RotatePasswordAsync(settings, cancellationToken).ConfigureAwait(false);
        _cachedShareHealth = result.Health;
        _shareHealthCheckedAt = DateTimeOffset.UtcNow;
        AddEvent(result.Result.Success ? "success" : "error", "share_password_rotated", result.Result.Message);
        return result;
    }

    public async Task<OperationResult> RunDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var failed = status.Gates.Where(gate => gate.Level == GateLevel.Fail).ToArray();
        if (failed.Length > 0)
        {
            var message = "Diagnostyka FAIL: " + string.Join("; ", failed.Select(gate => gate.Label + " — " + gate.Detail));
            AddEvent("error", "diagnostics_failed", message);
            return OperationResult.Fail("diagnostics_failed", message);
        }

        var result = status.State == RouterOperationalState.On
            ? "Diagnostyka lokalna PASS: hotspot, WFP i SMB są spójne. Nadal wymagany jest test z laptopa."
            : "Diagnostyka konfiguracji PASS; router jest wyłączony.";
        AddEvent("success", "diagnostics_passed", result);
        return OperationResult.Ok(result);
    }

    public IReadOnlyList<RouterEvent> GetEvents(long afterId = 0) =>
        _events.Where(entry => entry.Id > afterId).OrderBy(entry => entry.Id).ToArray();

    public void MarkPrimaryClient(string macAddress)
    {
        _usage.MarkPrimary(macAddress);
        AddEvent("info", "primary_client_changed", "Zmieniono urządzenie oznaczone jako laptop firmowy.");
    }

    public async Task WatchdogTickAsync(CancellationToken cancellationToken)
    {
        if (_state != RouterOperationalState.On || _topology is null)
        {
            return;
        }

        var health = await _isolation.InspectAsync(_topology, cancellationToken).ConfigureAwait(false);
        var hotspot = await _hotspot.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!hotspot.IsRunning)
        {
            // Some Windows/driver combinations stop an idle Wi-Fi Direct hotspot
            // even after reporting that the no-connections timeout is disabled.
            // With no hotspot there can be no WORK clients, so it is safe to remove
            // the stale policy and rebuild the complete quarantine -> hotspot -> WFP
            // sequence. A live hotspot with damaged filters remains fail-closed below.
            await RecoverFromWatchdogTripAsync(
                cancellationToken,
                "warning",
                "watchdog_hotspot_lost",
                "Windows zatrzymał hotspot; rozpoczynam bezpieczne odtworzenie pełnej ochrony.",
                "watchdog_hotspot_recovered",
                "Hotspot i kompletna izolacja zostały bezpiecznie odtworzone.").ConfigureAwait(false);
            return;
        }

        if (!health.Active || !health.FiltersPresent || !health.Ipv4Protected || !health.Ipv6Protected)
        {
            await RecoverFromWatchdogTripAsync(
                cancellationToken,
                "critical",
                "watchdog_trip",
                "Strażnik wykrył utratę ochrony; hotspot zostaje natychmiast wyłączony przed odbudową filtrów.",
                "watchdog_protection_recovered",
                "Hotspot uruchomiono ponownie dopiero po odtworzeniu i potwierdzeniu pełnej izolacji.").ConfigureAwait(false);
        }
        else
        {
            await _usage.UpdateClientsAsync(hotspot.Clients, cancellationToken).ConfigureAwait(false);
            _traffic?.UpdateClients(hotspot.Clients);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_state == RouterOperationalState.On)
        {
            await EmergencyStopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await _usage.DisposeAsync().ConfigureAwait(false);
        if (_traffic is not null) await _traffic.DisposeAsync().ConfigureAwait(false);
        await _isolation.DisposeAsync().ConfigureAwait(false);
        _shareInspectionGate.Dispose();
        _operationGate.Dispose();
    }

    private async Task EmergencyStopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _hotspot.StopAsync(cancellationToken).ConfigureAwait(false);
            await _usage.StopAsync(cancellationToken).ConfigureAwait(false);
            if (_traffic is not null) await _traffic.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddEvent("critical", "emergency_stop_failed", exception.Message);
        }
        // Deliberately retain quarantine/persistent WFP filters after an emergency.
    }

    private async Task RecoverFromWatchdogTripAsync(
        CancellationToken cancellationToken,
        string tripLevel,
        string tripCode,
        string tripMessage,
        string recoveredCode,
        string recoveredMessage)
    {
        var restart = false;
        await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_state != RouterOperationalState.On)
                return;

            AddEvent(tripLevel, tripCode, tripMessage);
            // Stop the hotspot first. Only once no WORK client can pass traffic may
            // the stale WFP policy be removed and rebuilt from a fresh quarantine.
            await EmergencyStopAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await _isolation.RemoveAsync(CancellationToken.None).ConfigureAwait(false);
                _topology = null;
                _state = RouterOperationalState.Off;
                restart = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _state = RouterOperationalState.Faulted;
                AddEvent("critical", "watchdog_recovery_cleanup_failed", exception.Message);
            }
        }
        finally
        {
            _operationGate.Release();
        }

        if (!restart)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        var result = await StartAsync(cancellationToken).ConfigureAwait(false);
        AddEvent(
            result.Success ? "success" : "critical",
            result.Success ? recoveredCode : "watchdog_recovery_failed",
            result.Success ? recoveredMessage : result.Message);
    }

    private async Task<HotspotSnapshot> SafeInspectHotspotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _hotspot.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HotspotSnapshot(false, false, 0, 0, "Error", exception.Message, null, Array.Empty<HotspotClientSnapshot>());
        }
    }

    private async Task<IsolationHealth> SafeInspectIsolationAsync(NetworkTopology? topology, CancellationToken cancellationToken)
    {
        try
        {
            return await _isolation.InspectAsync(topology, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new IsolationHealth(false, false, false, false, exception.Message);
        }
    }

    private async Task<ShareHealth> SafeInspectShareAsync(RouterSettings settings, CancellationToken cancellationToken)
    {
        if (_cachedShareHealth is not null && DateTimeOffset.UtcNow - _shareHealthCheckedAt < TimeSpan.FromSeconds(15))
            return _cachedShareHealth;

        await _shareInspectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedShareHealth is not null && DateTimeOffset.UtcNow - _shareHealthCheckedAt < TimeSpan.FromSeconds(15))
                return _cachedShareHealth;
            _cachedShareHealth = await _share.InspectAsync(settings, cancellationToken).ConfigureAwait(false);
            _shareHealthCheckedAt = DateTimeOffset.UtcNow;
            return _cachedShareHealth;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ShareHealth(false, false, false, false, false, @"\\<brama-WORK>\Firmowe", exception.Message);
        }
        finally
        {
            _shareInspectionGate.Release();
        }
    }

    private static IReadOnlyList<GateState> BuildGates(HotspotSnapshot hotspot, IsolationHealth isolation, ShareHealth share) =>
        new[]
        {
            new GateState("ethernet", "Internet przez Ethernet", hotspot.IsSupported ? GateLevel.Pass : GateLevel.Fail, hotspot.Failure ?? hotspot.Capability),
            new GateState("hotspot", "Mobilny hotspot", hotspot.IsRunning ? GateLevel.Pass : GateLevel.Unknown, hotspot.IsRunning ? $"Aktywny, klienci: {hotspot.ClientCount}/{hotspot.MaxClientCount}" : "Wyłączony"),
            new GateState("wfp-ipv4", "Izolacja IPv4", isolation.Ipv4Protected ? GateLevel.Pass : hotspot.IsRunning ? GateLevel.Fail : GateLevel.Unknown, isolation.Detail),
            new GateState("wfp-ipv6", "Blokada IPv6", isolation.Ipv6Protected ? GateLevel.Pass : hotspot.IsRunning ? GateLevel.Fail : GateLevel.Unknown, isolation.Detail),
            new GateState("smb", "Udział Firmowe", share.Ready ? GateLevel.Pass : GateLevel.Fail, share.Detail)
        };

    private static string SummaryFor(RouterOperationalState state, IReadOnlyList<GateState> gates) => state switch
    {
        RouterOperationalState.On when gates.Any(gate => gate.Level == GateLevel.Fail) => "Router działa, ale ochrona wymaga interwencji.",
        RouterOperationalState.On => "Hotspot działa i przechodzi lokalne bramki bezpieczeństwa.",
        RouterOperationalState.Starting => "Uruchamianie ochrony i hotspotu…",
        RouterOperationalState.Stopping => "Bezpieczne zatrzymywanie routera…",
        RouterOperationalState.Faulted => "Router został zatrzymany po błędzie bezpieczeństwa.",
        _ => "Router jest wyłączony."
    };

    private void AddEvent(string level, string code, string message)
    {
        _events.Enqueue(new RouterEvent(Interlocked.Increment(ref _eventId), DateTimeOffset.UtcNow, level, code, message));
        while (_events.Count > 500 && _events.TryDequeue(out _))
        {
        }
    }

    private static bool IsCurrentProcessAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

}
