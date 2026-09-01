using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Application.ScriptApproval.Queries.GetApprovedScripts;

public class GetUpgradeScriptsOverviewQueryHandler : IRequestHandler<GetUpgradeScriptsOverviewQuery, UpgradeScriptsOverviewDto>
{
    private readonly IScriptApprovalSourceClient _sourceClient;
    private readonly IGitHubSettingsProvider _gitHubSettings;
    private readonly IApprovedScriptRepository _approvedScripts;
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly IArtifactSigningService _artifactSigningService;

    public GetUpgradeScriptsOverviewQueryHandler(
        IScriptApprovalSourceClient sourceClient,
        IGitHubSettingsProvider gitHubSettings,
        IApprovedScriptRepository approvedScripts,
        IUpgradePathRepository upgradePaths,
        IArtifactSigningService artifactSigningService)
    {
        _sourceClient = sourceClient;
        _gitHubSettings = gitHubSettings;
        _approvedScripts = approvedScripts;
        _upgradePaths = upgradePaths;
        _artifactSigningService = artifactSigningService;
    }

    public async Task<UpgradeScriptsOverviewDto> Handle(
        GetUpgradeScriptsOverviewQuery request, CancellationToken cancellationToken)
    {
        // GetStatusAsync never throws — an unreachable GitHub comes back as UnavailableReason — so the
        // rest of the page still renders, with the already-imported corpus and the local scripts
        // intact. Same contract the Clients page relies on.
        var status = await _sourceClient.GetStatusAsync(cancellationToken);
        // Whether an approval can be published is configuration, not something to ask the publisher:
        // it no longer exposes a synchronous property, because a settings-page value must not be
        // captured at construction. See GitHubSettings.
        var gitHub = await _gitHubSettings.GetAsync(cancellationToken);
        var thisServer = _artifactSigningService.GetPublicKeyFingerprint();

        var approved = await _approvedScripts.GetAllAsync(cancellationToken);
        var scriptRows = await _upgradePaths.GetScriptUpgradePathsAsync(cancellationToken);
        var unsignedRows = await _upgradePaths.GetRowsWithoutScriptSignatureAsync(cancellationToken);

        // Hashed once here rather than per comparison below: the same local script is checked against
        // the corpus, and the same corpus entry against every local row.
        var localHashes = scriptRows
            .Where(r => r.Script is not null)
            .ToDictionary(r => (r.ApplicationName, r.Platform), r => ScriptContentHash.Of(r.Script!));
        var localHashSet = localHashes.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var approvedHashes = approved.Select(a => a.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var localScripts = scriptRows
            .Where(r => r.Script is not null)
            .Select(r =>
            {
                var hash = localHashes[(r.ApplicationName, r.Platform)];
                return new LocalScriptDto(r.ApplicationName, r.Platform, hash, r.ScriptSignature is not null, approvedHashes.Contains(hash));
            })
            .OrderBy(s => s.Signed)
            .ThenBy(s => s.ApplicationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Platform, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var candidates = new List<AdoptionCandidateDto>();
        foreach (var row in unsignedRows)
        {
            var rowLanguage = ScriptLanguages.For(row.Platform);

            foreach (var entry in approved.Where(a =>
                string.Equals(a.ApplicationName, row.ApplicationName, StringComparison.OrdinalIgnoreCase)))
            {
                // Filtered out of the offer entirely rather than offered and then refused by the
                // command: a candidate on screen that cannot be adopted is worse than one that was
                // never shown.
                if (ScriptLanguages.For(entry.PlatformBucket) != rowLanguage)
                {
                    continue;
                }

                // Already the content this row holds — a refresh will bless it, so there is nothing
                // for a human to decide here.
                if (row.Script is not null && string.Equals(ScriptContentHash.Of(row.Script), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add(new AdoptionCandidateDto(
                    row.ApplicationName,
                    row.Platform,
                    entry.Sha256,
                    entry.SignerFingerprint,
                    string.Equals(entry.SignerFingerprint, thisServer, StringComparison.OrdinalIgnoreCase),
                    entry.SignedBy,
                    entry.ApprovedAtUtc,
                    // Flagged so the page can say so on the button. The row is unsigned either way, so
                    // no agent is running what would be replaced, but an operator who wrote a script
                    // here and hasn't signed it yet should not lose it to a click labelled "adopt".
                    row.Script is not null));
            }
        }

        return new UpgradeScriptsOverviewDto(
            status.Repository,
            status.DefaultBranch,
            status.HeadCommitSha,
            status.UnavailableReason,
            gitHub.CanPublishScriptApprovals,
            thisServer,
            approved
                .Select(a => new ApprovedScriptDto(
                    a.Sha256, a.PlatformBucket, a.ApplicationName, a.SignerFingerprint,
                    string.Equals(a.SignerFingerprint, thisServer, StringComparison.OrdinalIgnoreCase),
                    a.SignedBy, a.ApprovedAtUtc, a.SourceCommitSha,
                    localHashSet.Contains(a.Sha256)))
                .ToList(),
            localScripts,
            candidates
                .OrderBy(c => c.ApplicationName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Platform, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }
}
