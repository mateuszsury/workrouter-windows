using WorkRouter.Models;

namespace WorkRouter.Abstractions;

public interface IHotspotController
{
    Task<HotspotSnapshot> InspectAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<int>> GetCandidatePrivateInterfaceIndexesAsync(CancellationToken cancellationToken);
    Task<HotspotSnapshot> StartAsync(RouterSettings settings, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface INetworkIsolation : IAsyncDisposable
{
    Task EnterQuarantineAsync(IReadOnlyList<int> interfaceIndexes, CancellationToken cancellationToken);
    Task ActivateAsync(NetworkTopology topology, CancellationToken cancellationToken);
    Task<IsolationHealth> InspectAsync(NetworkTopology? topology, CancellationToken cancellationToken);
    Task RemoveAsync(CancellationToken cancellationToken);
}

public interface IShareManager
{
    Task<ShareProvisionResult> EnsureAsync(RouterSettings settings, CancellationToken cancellationToken);
    Task<ShareProvisionResult> RotatePasswordAsync(RouterSettings settings, CancellationToken cancellationToken);
    Task<ShareHealth> InspectAsync(RouterSettings settings, CancellationToken cancellationToken);
}

public interface IUsageMonitor : IAsyncDisposable
{
    Task StartAsync(NetworkTopology topology, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task UpdateClientsAsync(IReadOnlyList<HotspotClientSnapshot> clients, CancellationToken cancellationToken);
    IReadOnlyList<ClientUsage> Snapshot();
    void MarkPrimary(string macAddress);
}

public interface IRouterCoordinator
{
    Task<RouterStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<OperationResult> StartAsync(CancellationToken cancellationToken);
    Task<OperationResult> StopAsync(CancellationToken cancellationToken);
    Task<OperationResult> UpdateSettingsAsync(RouterSettings settings, CancellationToken cancellationToken);
    Task<ShareProvisionResult> RotateSharePasswordAsync(CancellationToken cancellationToken);
    Task<OperationResult> RunDiagnosticsAsync(CancellationToken cancellationToken);
    IReadOnlyList<RouterEvent> GetEvents(long afterId = 0);
    void MarkPrimaryClient(string macAddress);
}
