using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Fronts the background upgrade-path scanner: lets a request trigger one without waiting for it,
/// and lets the UI (or the agent) poll its progress. The scanner itself runs out-of-process from
/// any particular HTTP request, so results are visible incrementally as each application resolves,
/// rather than all at once at the end of a run that — across hundreds of applications — could
/// otherwise take far longer than any single request should block for.
/// </summary>
public interface IUpgradePathScanCoordinator
{
    /// <returns>false if a scan is already running — the existing one keeps going, nothing new is queued.</returns>
    bool TryRequestStart();

    UpgradePathScanStatusDto GetStatus();
}
