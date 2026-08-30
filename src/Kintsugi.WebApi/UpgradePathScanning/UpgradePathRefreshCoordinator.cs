using System.Collections.Concurrent;
using System.Threading.Channels;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths.Commands.RefreshUpgradePath;

namespace Kintsugi.WebApi.UpgradePathScanning;

public record UpgradePathRefreshWorkItem(string ApplicationName, string? Platform, string? PromptOverride);

/// <summary>
/// In-memory coordinator between per-application "refresh" requests (the Applications page's
/// per-row "Send to AI" action) and <see cref="UpgradePathRefreshBackgroundService"/>, which
/// actually runs them — keyed by application name so many different applications can be
/// refreshing concurrently, each independently pollable, unlike the single fleet-wide
/// <see cref="UpgradePathScanCoordinator"/>. State lives only for the app's lifetime — a restart
/// mid-refresh loses progress, but not any already-persisted <c>UpgradePath</c> rows.
/// </summary>
public class UpgradePathRefreshCoordinator : IUpgradePathRefreshCoordinator
{
    private sealed class JobState
    {
        public bool IsRunning;
        public DateTimeOffset? StartedUtc;
        public DateTimeOffset? CompletedUtc;
        public RefreshUpgradePathResult? Result;
    }

    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<UpgradePathRefreshWorkItem> _queue = Channel.CreateUnbounded<UpgradePathRefreshWorkItem>();

    public bool TryStart(string applicationName, string? platform, string? promptOverride)
    {
        var state = _jobs.GetOrAdd(applicationName, _ => new JobState());

        lock (state)
        {
            if (state.IsRunning)
            {
                return false;
            }

            state.IsRunning = true;
            state.StartedUtc = DateTimeOffset.UtcNow;
            state.CompletedUtc = null;
            state.Result = null;
        }

        _queue.Writer.TryWrite(new UpgradePathRefreshWorkItem(applicationName, platform, promptOverride));
        return true;
    }

    public IAsyncEnumerable<UpgradePathRefreshWorkItem> ReadAllAsync(CancellationToken cancellationToken) =>
        _queue.Reader.ReadAllAsync(cancellationToken);

    public void Complete(string applicationName, RefreshUpgradePathResult result)
    {
        if (!_jobs.TryGetValue(applicationName, out var state))
        {
            return;
        }

        lock (state)
        {
            state.IsRunning = false;
            state.CompletedUtc = DateTimeOffset.UtcNow;
            state.Result = result;
        }
    }

    public void Fault(string applicationName, string errorMessage) =>
        Complete(applicationName, new RefreshUpgradePathResult(false, errorMessage));

    public UpgradePathRefreshStatusDto GetStatus(string applicationName)
    {
        if (!_jobs.TryGetValue(applicationName, out var state))
        {
            return new UpgradePathRefreshStatusDto(false, null, null, null);
        }

        lock (state)
        {
            return new UpgradePathRefreshStatusDto(state.IsRunning, state.StartedUtc, state.CompletedUtc, state.Result);
        }
    }
}
