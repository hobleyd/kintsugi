using Kintsugi.Application.HostActivity;

namespace Kintsugi.WebApi.HostActivity;

/// <summary>Once a day, deletes host activity older than the configured retention.</summary>
public class HostEventPruneBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HostEventPruneBackgroundService> _logger;

    public HostEventPruneBackgroundService(IServiceScopeFactory scopeFactory, ILogger<HostEventPruneBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short wait first so a restart loop does not run it on every start, then daily.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pruner = scope.ServiceProvider.GetRequiredService<HostEventPruner>();
                var removed = await pruner.PruneAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (removed > 0)
                {
                    _logger.LogInformation("Removed {Count} host activity events past their retention", removed);
                }

                var logs = scope.ServiceProvider.GetRequiredService<Kintsugi.Application.LogCollections.LogCollectionPruner>();
                var removedLogs = await logs.PruneAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (removedLogs > 0)
                {
                    _logger.LogInformation("Removed {Count} collected device logs past their 30 days", removedLogs);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Host activity cleanup failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
