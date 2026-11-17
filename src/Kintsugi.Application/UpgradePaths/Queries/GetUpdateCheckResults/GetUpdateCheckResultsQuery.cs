using MediatR;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpdateCheckResults;

/// <summary>Every row of the current (or last) "Check for Updates" run, asked for once it finishes.</summary>
public record GetUpdateCheckResultsQuery : IRequest<UpgradePathRunResultsDto>;
