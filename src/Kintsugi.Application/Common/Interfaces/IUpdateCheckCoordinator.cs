using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Fronts the background "Check for Updates" runner: lets a request trigger a run over every
/// already-resolved script upgrade path without waiting for it, and lets the UI poll its progress.
/// Mirrors <see cref="IUpgradePathScanCoordinator"/>, but for re-checking existing script versions
/// (no AI call) rather than researching new upgrade paths.
/// </summary>
public interface IUpdateCheckCoordinator
{
    /// <returns>false if a run is already in progress — the existing one keeps going, nothing new is queued.</returns>
    bool TryRequestStart();

    UpdateCheckStatusDto GetStatus();

    /// <summary>A row per application the run has reported on, with its outcome — what the status's
    /// counts and notes cannot say. Fetched once, when the run finishes.</summary>
    UpgradePathRunResultsDto GetResults();
}
