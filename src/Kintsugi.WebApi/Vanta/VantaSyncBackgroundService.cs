using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vanta;
using Kintsugi.Application.Vanta.Commands.SyncVantaResources;

namespace Kintsugi.WebApi.Vanta;

/// <summary>
/// Runs the Vanta sync on its configured interval, and immediately whenever the settings screen's
/// "Sync now" signals the coordinator.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the three upgrade-path background services, which only ever wait to be asked, this one
/// also has a clock. It still goes through the same coordinator, so a scheduled run and a manual one
/// can never overlap — which matters more for Vanta than elsewhere, because issuing a second access
/// token revokes the first.
/// </para>
/// <para>
/// The wait is capped well below a typical interval so that a settings change — a different
/// interval, or the integration being switched on — is picked up within minutes rather than at the
/// end of whatever wait was already in progress.
/// </para>
/// </remarks>
public class VantaSyncBackgroundService : BackgroundService
{
    /// <summary>Longest this ever sleeps in one go, so configuration edits take effect promptly.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    /// <summary>Grace period before the first run of a process. Long enough that a restart during a
    /// deployment does not fire a sync into a half-migrated database, short enough that an
    /// administrator restarting to fix a configuration sees the result while still watching.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private readonly VantaSyncCoordinator _coordinator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VantaSyncBackgroundService> _logger;

    public VantaSyncBackgroundService(
        VantaSyncCoordinator coordinator, IServiceScopeFactory scopeFactory, ILogger<VantaSyncBackgroundService> logger)
    {
        _coordinator = coordinator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await NextWaitAsync(startedUtc, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Reading the settings failed — most plausibly the database is not up yet. Back off
                // and try again rather than spinning; nothing has been sent, so nothing is stale.
                _logger.LogWarning(ex, "Could not read the Vanta settings; retrying shortly");
                wait = MaxWait;
            }

            bool signalled;
            try
            {
                signalled = await _coordinator.WaitForSignalAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!signalled)
            {
                // Woke up early only to re-read the configuration, and nothing is due yet.
                if (wait > TimeSpan.Zero)
                {
                    continue;
                }

                // The timer is what is asking, so the claim has to be made here — and can still be
                // refused, if a manual run started in the moment between the wait ending and this
                // line. A signalled wake-up needs no claim: "Sync now" already made it through
                // TryRequestStart, which is what let it return true to the browser.
                if (!_coordinator.TryStartScheduledRun())
                {
                    continue;
                }
            }

            try
            {
                await RunSyncAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _coordinator.Fault("The sync was cancelled because the server is shutting down.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vanta sync failed unexpectedly");
                _coordinator.Fault($"The sync failed unexpectedly: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// How long to wait before the next scheduled run: zero when one is due now. Reads the settings
    /// every time round the loop rather than caching them, because the interval and the enabled flag
    /// are both editable while this is running.
    /// </summary>
    private async Task<TimeSpan> NextWaitAsync(DateTimeOffset processStartedUtc, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IVantaSettingsProvider>().GetAsync(cancellationToken);

        if (!settings.CanSync)
        {
            // Switched off or half-configured: nothing to schedule. Still returns a bounded wait
            // rather than an infinite one, so switching it on takes effect without a restart.
            return MaxWait;
        }

        var last = _coordinator.LastCompletedUtc;
        var due = last is null
            ? processStartedUtc + StartupDelay
            : last.Value + TimeSpan.FromHours(settings.SyncIntervalHours);

        var remaining = due - DateTimeOffset.UtcNow;
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : (remaining < MaxWait ? remaining : MaxWait);
    }

    private async Task RunSyncAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.Send(new SyncVantaResourcesCommand(), stoppingToken);

        if (!result.Succeeded && result.Attempted)
        {
            _logger.LogWarning("Vanta sync did not complete: {Message}", result.Message);
        }

        _coordinator.Complete(result);
    }
}
