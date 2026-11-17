using MediatR;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScanResults;

/// <summary>Every row of the current (or last) "Find Upgrade Paths" run, asked for once it finishes.</summary>
public record GetUpgradePathScanResultsQuery : IRequest<UpgradePathRunResultsDto>;
