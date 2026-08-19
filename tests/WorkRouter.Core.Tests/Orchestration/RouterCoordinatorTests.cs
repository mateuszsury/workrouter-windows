using System.Net;
using WorkRouter.Abstractions;
using WorkRouter.Configuration;
using WorkRouter.Core.Networking;
using WorkRouter.Models;
using WorkRouter.Orchestration;

namespace WorkRouter.Tests.Orchestration;

public sealed class RouterCoordinatorTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkRouterTests", Guid.NewGuid().ToString("N"));
    private readonly FakeHotspot _hotspot = new();
    private readonly FakeIsolation _isolation = new();
    private readonly FakeShare _share = new();
    private readonly FakeUsage _usage = new();
    private RouterCoordinator _coordinator = null!;

    public async Task InitializeAsync()
    {
        Calls.All.Clear();
        var configuration = new RouterConfigurationStore(_root);
        await configuration.SaveAsync(new RouterSettings(Passphrase: "ValidPassword!123"), CancellationToken.None);
        _coordinator = new RouterCoordinator(_hotspot, _isolation, _share, _usage, configuration, () => true);
    }

    [Fact]
    public async Task Start_InstallsQuarantineBeforeStartingHotspot()
    {
        var result = await _coordinator.StartAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "share", "candidates", "quarantine", "hotspot-start", "activate", "usage-start", "clients" }, Calls.All);
        var status = await _coordinator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(RouterOperationalState.On, status.State);
    }

    [Fact]
    public async Task Start_WhenIsolationCannotBeVerified_StopsHotspotAndRetainsQuarantine()
    {
        _isolation.FailInspection = true;

        var result = await _coordinator.StartAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("hotspot-stop", Calls.All);
        Assert.DoesNotContain("remove", Calls.All);
        Assert.False(_hotspot.Running);
        Assert.True(_isolation.Quarantined);
        var status = await _coordinator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(RouterOperationalState.Faulted, status.State);
    }

    [Fact]
    public async Task Start_WhenCancelled_StopsHotspotAndRetainsQuarantine()
    {
        _hotspot.CancelStart = true;

        await Assert.ThrowsAsync<OperationCanceledException>(() => _coordinator.StartAsync(CancellationToken.None));

        Assert.Contains("hotspot-stop", Calls.All);
        Assert.DoesNotContain("remove", Calls.All);
        Assert.True(_isolation.Quarantined);
    }

    [Fact]
    public async Task Stop_RemovesFiltersOnlyAfterHotspotStops()
    {
        Assert.True((await _coordinator.StartAsync(CancellationToken.None)).Success);
        Calls.All.Clear();

        var result = await _coordinator.StopAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "hotspot-stop", "usage-stop", "remove" }, Calls.All);
    }

    [Fact]
    public async Task Watchdog_RecoversUnexpectedHotspotStopThroughFullSafeStartSequence()
    {
        Assert.True((await _coordinator.StartAsync(CancellationToken.None)).Success);
        _hotspot.SimulateUnexpectedStop();

        var beforeWatchdog = await _coordinator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(RouterOperationalState.On, beforeWatchdog.State);

        await _coordinator.WatchdogTickAsync(CancellationToken.None);
        var afterWatchdog = await _coordinator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(RouterOperationalState.On, afterWatchdog.State);
        Assert.True(_hotspot.Running);
        Assert.Contains("remove", Calls.All);
        Assert.Contains(_coordinator.GetEvents(), entry => entry.Code == "watchdog_hotspot_recovered");
    }

    [Fact]
    public async Task Watchdog_StopsHotspotBeforeRecoveringTransientIsolationLoss()
    {
        Assert.True((await _coordinator.StartAsync(CancellationToken.None)).Success);
        _isolation.FailNextInspection = true;
        Calls.All.Clear();

        await _coordinator.WatchdogTickAsync(CancellationToken.None);

        var status = await _coordinator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(RouterOperationalState.On, status.State);
        Assert.True(_hotspot.Running);
        Assert.True(Calls.All.IndexOf("hotspot-stop") < Calls.All.IndexOf("remove"));
        Assert.Contains(_coordinator.GetEvents(), entry => entry.Code == "watchdog_trip");
        Assert.Contains(_coordinator.GetEvents(), entry => entry.Code == "watchdog_protection_recovered");
    }

    [Fact]
    public async Task Status_ReportsObservedActiveBandSeparatelyFromConfiguredBand()
    {
        _hotspot.ActiveBand = "FiveGigahertz";
        await _coordinator.StartAsync(CancellationToken.None);

        var status = await _coordinator.GetStatusAsync(CancellationToken.None);

        Assert.Equal("FiveGigahertz", status.ActiveBand);
        Assert.Equal("FiveGigahertz", status.Settings.Band);
    }

    [Fact]
    public async Task UpdateSettings_ProvisionsShareBeforeCommitAndLeavesOldSettingsOnFailure()
    {
        var next = new RouterSettings(Passphrase: "NewPassword!123");
        var result = await _coordinator.UpdateSettingsAsync(next, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(next.Passphrase, _share.LastSettings?.Passphrase);
        var saved = await new RouterConfigurationStore(_root).LoadAsync(CancellationToken.None);
        Assert.Equal(next.Passphrase, saved.Passphrase);

        _share.FailEnsure = true;
        var failed = await _coordinator.UpdateSettingsAsync(next with { Passphrase = "OtherPassword!123" }, CancellationToken.None);

        Assert.False(failed.Success);
        Assert.Equal("share_password_sync_failed", failed.Code);
        saved = await new RouterConfigurationStore(_root).LoadAsync(CancellationToken.None);
        Assert.Equal(next.Passphrase, saved.Passphrase);
    }

    public async Task DisposeAsync()
    {
        await _coordinator.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static class Calls
    {
        public static readonly List<string> All = new();
    }

    private sealed class FakeHotspot : IHotspotController
    {
        public bool Running { get; private set; }
        public string? ActiveBand { get; set; }
        public bool CancelStart { get; set; }
        public void SimulateUnexpectedStop() => Running = false;
        private static readonly NetworkTopology Topology = new(
            42,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            IPAddress.Parse("192.168.137.1"),
            NetworkAddressing.Parse("192.168.137.0/24"),
            "Połączenie lokalne* 2",
            new[] { 41, 42 });

        public Task<HotspotSnapshot> InspectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HotspotSnapshot(true, Running, 0, 8, "Enabled", null, Running ? Topology : null, Array.Empty<HotspotClientSnapshot>(), ActiveBand));

        public Task<IReadOnlyList<int>> GetCandidatePrivateInterfaceIndexesAsync(CancellationToken cancellationToken)
        {
            Calls.All.Add("candidates");
            return Task.FromResult<IReadOnlyList<int>>(new[] { 41, 42 });
        }

        public Task<HotspotSnapshot> StartAsync(RouterSettings settings, CancellationToken cancellationToken)
        {
            Calls.All.Add("hotspot-start");
            Running = true;
            if (CancelStart)
                throw new OperationCanceledException();
            return InspectAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Calls.All.Add("hotspot-stop");
            Running = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeIsolation : INetworkIsolation
    {
        public bool Quarantined { get; private set; }
        public bool Active { get; private set; }
        public bool FailInspection { get; set; }
        public bool FailNextInspection { get; set; }

        public Task EnterQuarantineAsync(IReadOnlyList<int> interfaceIndexes, CancellationToken cancellationToken)
        {
            Calls.All.Add("quarantine");
            Quarantined = true;
            return Task.CompletedTask;
        }

        public Task ActivateAsync(NetworkTopology topology, CancellationToken cancellationToken)
        {
            Calls.All.Add("activate");
            Active = true;
            return Task.CompletedTask;
        }

        public Task<IsolationHealth> InspectAsync(NetworkTopology? topology, CancellationToken cancellationToken)
        {
            if (FailInspection || FailNextInspection)
            {
                FailNextInspection = false;
                return Task.FromResult(new IsolationHealth(false, false, false, false, "missing"));
            }
            return Task.FromResult(new IsolationHealth(Active, Active || Quarantined, Active, Active, "ok"));
        }

        public Task RemoveAsync(CancellationToken cancellationToken)
        {
            Calls.All.Add("remove");
            Active = false;
            Quarantined = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeShare : IShareManager
    {
        private static readonly ShareHealth Health = new(true, true, true, true, true, @"\\192.168.137.1\Firmowe", "ok");
        public RouterSettings? LastSettings { get; private set; }
        public bool FailEnsure { get; set; }

        public Task<ShareProvisionResult> EnsureAsync(RouterSettings settings, CancellationToken cancellationToken)
        {
            Calls.All.Add("share");
            LastSettings = settings;
            if (FailEnsure)
                return Task.FromResult(new ShareProvisionResult(OperationResult.Fail("share_failed", "share failed"), Health, null));
            return Task.FromResult(new ShareProvisionResult(OperationResult.Ok("ok"), Health, null));
        }

        public Task<ShareProvisionResult> RotatePasswordAsync(RouterSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(new ShareProvisionResult(OperationResult.Ok("ok"), Health, "new-password"));

        public Task<ShareHealth> InspectAsync(RouterSettings settings, CancellationToken cancellationToken) => Task.FromResult(Health);
    }

    private sealed class FakeUsage : IUsageMonitor
    {
        public Task StartAsync(NetworkTopology topology, CancellationToken cancellationToken)
        {
            Calls.All.Add("usage-start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Calls.All.Add("usage-stop");
            return Task.CompletedTask;
        }

        public Task UpdateClientsAsync(IReadOnlyList<HotspotClientSnapshot> clients, CancellationToken cancellationToken)
        {
            Calls.All.Add("clients");
            return Task.CompletedTask;
        }

        public IReadOnlyList<ClientUsage> Snapshot() => Array.Empty<ClientUsage>();
        public void MarkPrimary(string macAddress) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
