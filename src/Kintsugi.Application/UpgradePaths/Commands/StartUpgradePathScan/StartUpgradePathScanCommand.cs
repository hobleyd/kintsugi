using MediatR;

namespace Kintsugi.Application.UpgradePaths.Commands.StartUpgradePathScan;

/// <summary>Requests that the background upgrade-path scanner start a run. Returns immediately —
/// it does not wait for the scan to finish. Backs the "Find Upgrade Paths" button.</summary>
public record StartUpgradePathScanCommand : IRequest<StartUpgradePathScanResult>;

public record StartUpgradePathScanResult(bool Started, UpgradePathScanStatusDto Status);
