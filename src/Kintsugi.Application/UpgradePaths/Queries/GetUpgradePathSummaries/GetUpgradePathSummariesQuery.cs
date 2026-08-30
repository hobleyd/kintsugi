using MediatR;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathSummaries;

/// <summary>Lists every researched (application, platform) upgrade path with host counts
/// aggregated in, rather than one row per host — what the Applications page renders.</summary>
public record GetUpgradePathSummariesQuery : IRequest<IReadOnlyList<UpgradePathSummaryDto>>;
