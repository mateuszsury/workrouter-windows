using WorkRouter.Orchestration;

namespace WorkRouter.Service;

internal sealed class RouterWatchdogService : BackgroundService
{
    private readonly RouterCoordinator _coordinator;
    private readonly ServiceTokenManager _tokens;
    private readonly ILogger<RouterWatchdogService> _logger;

    public RouterWatchdogService(
        RouterCoordinator coordinator,
        ServiceTokenManager tokens,
        ILogger<RouterWatchdogService> logger)
    {
        _coordinator = coordinator;
        _tokens = tokens;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _tokens.WriteEndpointFileAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _coordinator.WatchdogTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Watchdog tick failed");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogCritical(exception, "Service shutdown could not confirm a clean router stop");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
