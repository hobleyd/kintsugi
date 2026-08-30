using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths.Commands.CheckApplicationUpdate;

namespace Kintsugi.WebApi.UpgradePathScanning;

/// <summary>
/// Waits for <see cref="UpdateCheckCoordinator.TryRequestStart"/> to signal a "Check for Updates"
/// run, then works through every already-resolved script upgrade path, re-running each one's own
/// <c>--update-version</c> mode — no AI call, so unlike the scan this fans out with modest
/// concurrency rather than running in series.
/// </summary>
public class UpdateCheckBackgroundService : BackgroundService
{
    private const int MaxConcurrency = 5;

    private readonly UpdateCheckCoordinator _coordinator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpdateCheckBackgroundService> _logger;

    public UpdateCheckBackgroundService(UpdateCheckCoordinator coordinator, IServiceScopeFactory scopeFactory, ILogger<UpdateCheckBackgroundService> logger)
    {
        _coordinator = coordinator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _coordinator.WaitForSignalAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunUpdateCheckAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Check for Updates run failed unexpectedly");
                _coordinator.Fault($"The run failed unexpectedly: {ex.Message}");
            }
        }
    }

    private async Task RunUpdateCheckAsync(CancellationToken stoppingToken)
    {
        List<(string ApplicationName, string Platform)> targets;
        using (var scope = _scopeFactory.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IUpgradePathRepository>();
            var paths = await repository.GetScriptUpgradePathsAsync(stoppingToken);
            targets = paths.Select(p => (p.ApplicationName, p.Platform)).ToList();
        }

        _coordinator.SetTotal(targets.Count);

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = stoppingToken },
            async (target, ct) =>
            {
                using var scope = _scopeFactory.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                CheckApplicationUpdateResult result;
                try
                {
                    result = await sender.Send(new CheckApplicationUpdateCommand(target.ApplicationName, target.Platform), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Update check for {ApplicationName} ({Platform}) failed unexpectedly", target.ApplicationName, target.Platform);
                    result = new CheckApplicationUpdateResult(target.ApplicationName, target.Platform, false, false, $"Unexpected error: {ex.Message}");
                }

                _coordinator.ReportItem(result);
            });

        _coordinator.Complete();
    }
}
