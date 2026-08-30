using MediatR;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScanStatus;

/// <summary>Polled by the UI while a scan runs, to show live progress.</summary>
public record GetUpgradePathScanStatusQuery : IRequest<UpgradePathScanStatusDto>;
