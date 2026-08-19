using WorkRouter.Configuration;
using WorkRouter.Orchestration;

namespace WorkRouter.Service;

internal sealed class RouterAutoStartService : BackgroundService
{
    private readonly TrafficPreferencesStore _preferences;
    private readonly RouterCoordinator _coordinator;
    private readonly ILogger<RouterAutoStartService> _logger;

    public RouterAutoStartService(TrafficPreferencesStore preferences, RouterCoordinator coordinator, ILogger<RouterAutoStartService> logger)
    { _preferences = preferences; _coordinator = coordinator; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            var preferences = await _preferences.LoadAsync(stoppingToken).ConfigureAwait(false);
            if (!preferences.AutoStartRouter) return;
            var result = await _coordinator.StartAsync(stoppingToken).ConfigureAwait(false);
            if (!result.Success) _logger.LogError("Autostart routera nieudany: {Code} {Message}", result.Code, result.Message);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogError(ex, "Autostart routera zakończył się wyjątkiem; brak ponowień."); }
    }
}
