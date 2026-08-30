using MediatR;
using Kintsugi.Application.UpgradePaths.Commands.RefreshUpgradePath;

namespace Kintsugi.WebApi.UpgradePathScanning;

/// <summary>
/// Consumes <see cref="UpgradePathRefreshCoordinator"/>'s work queue and runs each per-application
/// refresh (<see cref="RefreshUpgradePathCommand"/>) on its own DI scope (an EF Core
/// <c>DbContext</c> isn't safe to share across concurrent operations), with bounded concurrency
/// across whatever independent per-application jobs happen to be queued at once — separate from
/// the fleet-wide scanner's own budget, since these are on-demand user actions rather than one
/// coordinated batch.
/// </summary>
public class UpgradePathRefreshBackgroundService : BackgroundService
{
    // Kept modest and independent of the fleet scanner's own limit — both ultimately contend for
    // the same configured AI provider, and a refresh is usually just one or two applications a
    // person is actively waiting on, not a throughput-sensitive batch.
    private const int MaxConcurrency = 3;

    private readonly UpgradePathRefreshCoordinator _coordinator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpgradePathRefreshBackgroundService> _logger;

    public UpgradePathRefreshBackgroundService(UpgradePathRefreshCoordinator coordinator, IServiceScopeFactory scopeFactory, ILogger<UpgradePathRefreshBackgroundService> logger)
    {
        _coordinator = coordinator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var throttle = new SemaphoreSlim(MaxConcurrency);

        try
        {
            await foreach (var item in _coordinator.ReadAllAsync(stoppingToken))
            {
                await throttle.WaitAsync(stoppingToken);
                _ = RunOneAsync(item, throttle, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private async Task RunOneAsync(UpgradePathRefreshWorkItem item, SemaphoreSlim throttle, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            RefreshUpgradePathResult result;
            try
            {
                result = await sender.Send(new RefreshUpgradePathCommand(item.ApplicationName, item.Platform, item.PromptOverride), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Upgrade path refresh for {ApplicationName} failed unexpectedly", item.ApplicationName);
                result = new RefreshUpgradePathResult(false, $"Unexpected error: {ex.Message}");
            }

            _coordinator.Complete(item.ApplicationName, result);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown — leave the job's status as "running"; it lives only for the
            // app's lifetime anyway, same tradeoff as the fleet scanner.
        }
        finally
        {
            throttle.Release();
        }
    }
}
