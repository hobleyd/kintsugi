using MediatR;
using Kintsugi.Application.Autonomy.Commands.RecordKpiSnapshot;

namespace Kintsugi.WebApi.HostActivity;

/// <summary>
/// Records the KPIs once a day, from the first day the server runs with them. History cannot be rebuilt
/// afterwards, so this starts as early as it can; the command itself does nothing if today already has
/// its snapshot, so a restart or two instances cannot write twice.
/// </summary>
public class KpiSnapshotBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KpiSnapshotBackgroundService> _logger;

    public KpiSnapshotBackgroundService(IServiceScopeFactory scopeFactory, ILogger<KpiSnapshotBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the fleet settle after a start: hosts check in over the first minutes.
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
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
                var written = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RecordKpiSnapshotCommand(), stoppingToken);
                if (written > 0)
                {
                    _logger.LogInformation("Recorded {Count} KPI snapshots", written);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recording the KPI snapshots failed");
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
