using Kintsugi.Application.UpgradePaths.Commands.RefreshUpgradePath;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Fronts the background per-application upgrade-path refresh queue: lets a request enqueue one
/// (or find one already running) without waiting for it, and lets the UI poll its progress. Keyed
/// by application name — unlike the fleet-wide <see cref="IUpgradePathScanCoordinator"/>, many
/// refreshes for different applications can be in flight at once, each independently pollable,
/// since these are on-demand per-row actions rather than one coordinated batch.
/// </summary>
public interface IUpgradePathRefreshCoordinator
{
    /// <returns>false if a refresh is already running for this application — the existing one keeps
    /// going, nothing new is queued.</returns>
    bool TryStart(string applicationName, string? platform, string? promptOverride);

    UpgradePathRefreshStatusDto GetStatus(string applicationName);
}

public record UpgradePathRefreshStatusDto(
    bool IsRunning,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    RefreshUpgradePathResult? Result);
