using MediatR;
using Kintsugi.Application.UpgradePaths.Commands.ResearchApplicationUpgradePath;
using Kintsugi.Application.UpgradePaths.Queries.PrepareUpgradePathScan;
using Kintsugi.Domain.Enums;

namespace Kintsugi.WebApi.UpgradePathScanning;

/// <summary>
/// Waits for <see cref="UpgradePathScanCoordinator.TryRequestStart"/> to signal a scan, then runs
/// it: works through every (application, platform) that needs resolving one at a time — in series,
/// so the AI calls behind script generation never fan out against the configured provider's rate
/// limits — each on its own DI scope (an EF Core <c>DbContext</c> isn't safe to share across
/// concurrent operations), persisting and reporting each result as it lands rather than batching
/// until the whole scan — which, across hundreds of applications, could otherwise take far longer
/// than useful feedback should wait for — finishes.
/// </summary>
public class UpgradePathScanBackgroundService : BackgroundService
{
    private readonly UpgradePathScanCoordinator _coordinator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpgradePathScanBackgroundService> _logger;

    public UpgradePathScanBackgroundService(UpgradePathScanCoordinator coordinator, IServiceScopeFactory scopeFactory, ILogger<UpgradePathScanBackgroundService> logger)
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
                await RunScanAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upgrade path scan failed unexpectedly");
                _coordinator.Fault($"The scan failed unexpectedly: {ex.Message}");
            }
        }
    }

    private async Task RunScanAsync(CancellationToken stoppingToken)
    {
        UpgradePathScanPlan plan;
        using (var scope = _scopeFactory.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            plan = await sender.Send(new PrepareUpgradePathScanQuery(), stoppingToken);
        }

        // No global fault on a missing/disabled AI agent: package-manager-managed applications
        // resolve without it, so the scan still runs for those. A Research-kind item with
        // plan.Settings null just resolves to a per-item NotFound with an explanatory note, the
        // same way ResearchApplicationUpgradePathCommandHandler already handles an unrecognized
        // package manager or an unsupported platform — see GenerateScriptViaAiAsync.
        _coordinator.SetTotal(plan.WorkItems.Count);

        foreach (var item in plan.WorkItems)
        {
            stoppingToken.ThrowIfCancellationRequested();

            using var scope = _scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            ResearchApplicationUpgradePathResult result;
            try
            {
                result = await sender.Send(
                    new ResearchApplicationUpgradePathCommand(item.ApplicationName, item.Platform, item.KnownVersions, item.Kind, item.PackageManagerName, item.ApplicationIdentifier, plan.Settings),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Upgrade path research for {ApplicationName} ({Platform}) failed unexpectedly", item.ApplicationName, item.Platform);
                result = new ResearchApplicationUpgradePathResult(item.ApplicationName, item.Platform, UpgradePathStatus.Failed, false, $"Unexpected error: {ex.Message}");
            }

            _coordinator.ReportItem(result);
        }

        _coordinator.Complete();
    }
}
