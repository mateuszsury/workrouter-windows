using WorkRouter.Models;

namespace WorkRouter.Abstractions;

public interface ITrafficMonitor : IAsyncDisposable
{
    TrafficCaptureStatus Status { get; }
    Task StartAsync(NetworkTopology topology, TrafficPreferences preferences, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    void UpdatePreferences(TrafficPreferences preferences);
    void UpdateClients(IReadOnlyList<HotspotClientSnapshot> clients);
    TrafficSummary GetSummary(int windowMinutes = 60);
    IReadOnlyList<TrafficEvent> GetEvents(long afterId = 0);
    void Clear();
}

public interface IStartupShortcutManager
{
    Task<OperationResult> SetOpenPanelAtLoginAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> IsOpenPanelAtLoginEnabledAsync(CancellationToken cancellationToken);
}
