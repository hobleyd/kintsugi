using MediatR;
using Kintsugi.Application.UpgradePaths.Commands.ResearchApplicationUpgradePath;
using Kintsugi.Application.UpgradePaths.Queries.PrepareUpgradePathScan;

namespace Kintsugi.Application.UpgradePaths.Commands.RefreshUpgradePath;

public class RefreshUpgradePathCommandHandler : IRequestHandler<RefreshUpgradePathCommand, RefreshUpgradePathResult>
{
    private readonly ISender _sender;

    public RefreshUpgradePathCommandHandler(ISender sender)
    {
        _sender = sender;
    }

    public async Task<RefreshUpgradePathResult> Handle(RefreshUpgradePathCommand request, CancellationToken cancellationToken)
    {
        // No AiConfigured gate here: every application gets a work item in the plan regardless of
        // AI configuration (see PrepareUpgradePathScanQueryHandler), and ResearchApplicationUpgradePathCommandHandler
        // reports an unconfigured AI agent as a per-item NotFound note rather than a hard failure —
        // so a refresh on a package-manager-managed row is never blocked by AI being off.
        var plan = await _sender.Send(new PrepareUpgradePathScanQuery(), cancellationToken);

        var matching = plan.WorkItems
            .Where(item => item.ApplicationName.Equals(request.ApplicationName, StringComparison.OrdinalIgnoreCase)
                && (request.Platform is null || item.Platform.Equals(request.Platform, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matching.Count == 0)
        {
            return new RefreshUpgradePathResult(false, $"'{request.ApplicationName}' is not currently installed on any host.");
        }

        // Sequential rather than fanned out like the fleet-wide scan — a single row covers at most
        // a handful of platforms, so there's no throughput reason to add concurrency here.
        var results = new List<RefreshedUpgradePathDto>();
        foreach (var item in matching)
        {
            var outcome = await _sender.Send(
                new ResearchApplicationUpgradePathCommand(
                    item.ApplicationName, item.Platform, item.KnownVersions, item.Kind, item.PackageManagerName, item.ApplicationIdentifier, plan.Settings,
                    ForceRecheck: true, PromptOverride: request.PromptOverride),
                cancellationToken);

            results.Add(new RefreshedUpgradePathDto(
                outcome.ApplicationName, outcome.Platform, outcome.Status, outcome.LatestVersion, outcome.Method,
                outcome.DownloadUrl, outcome.Command, outcome.Instructions, outcome.SourceUrl, outcome.Note, outcome.CheckedUtc, outcome.Script));
        }

        return new RefreshUpgradePathResult(true, null, results);
    }
}
