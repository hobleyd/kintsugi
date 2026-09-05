using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;

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
        var scriptRows = (await _upgradePaths.GetScriptUpgradePathsAsync(cancellationToken))
            .Where(r => r.Script is not null)
            .ToList();
        var unsignedRows = await _upgradePaths.GetRowsWithoutScriptSignatureAsync(cancellationToken);

        // Hashed once here rather than per comparison below: the same local script is checked against
        // the corpus, and the same corpus entry against every local row.
        var localHashes = scriptRows.ToDictionary(r => (r.ApplicationName, r.Platform), r => ScriptContentHash.Of(r.Script!));
        var localHashSet = localHashes.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var approvedHashes = approved.Select(a => a.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // One entry per script, not per row — see LocalScriptDto. Rows under a recognized package
        // manager's bucket are grouped by (bucket, content, signed-or-not) and named for the manager;
        // every other row is its own entry. The unrecognized-manager case is deliberately in the
        // per-row half: nothing writes a script for one today, so a row that has one is worth seeing
        // as itself rather than being folded under a label this server has no builder for.
        var localScripts = scriptRows
            .GroupBy(r =>
            {
                var managerName = PlatformBucket.PackageManagerNameFrom(r.Platform);
                var shared = managerName is not null && PackageManagerCatalog.TryGet(managerName, out _);
                return (
                    Key: shared ? string.Empty : r.ApplicationName,
                    r.Platform,
                    Hash: localHashes[(r.ApplicationName, r.Platform)],
                    Signed: r.ScriptSignature is not null);
            })
            .Select(group =>
            {
                var rows = group.ToList();
                var script = rows[0].Script!;
                var name = group.Key.Key.Length > 0
                    ? group.Key.Key
                    : PackageManagerScriptLabel(group.Key.Platform, script, rows);

                // Compared against what this build writes, not against a version number: these
                // scripts carry none, and the content is the only thing a signature covers. Null for
                // an AI-researched row, which has no server-written counterpart to differ from. Asked
                // of every row in the group rather than its first because Homebrew's and Snap's two
                // builds are one text: a group there holds the manager's own row and its managed ones
                // together.
                var newerServerScript = rows.Any(r =>
                    PackageManagerCatalog.CurrentScriptFor(r.ApplicationName, r.Platform) is { } current
                    && !string.Equals(script, current, StringComparison.Ordinal));

                return new LocalScriptDto(
                    name, group.Key.Platform, group.Key.Hash, rows.Count, group.Key.Signed,
                    approvedHashes.Contains(group.Key.Hash), newerServerScript);
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

    /// <summary>
    /// What one package-manager script is called on the page: "Homebrew (any managed application)"
    /// or "Homebrew (self-update)", the same words the approval repository uses for the same bytes.
    /// </summary>
    /// <remarks>
    /// Which of the two it is comes from the bytes when they are one of this build's — the rule
    /// <see cref="ApprovedScriptIdentity"/> applies, for the same reason: the row is what must not
    /// be trusted. When they are neither (a revision an earlier build wrote, still signed and still
    /// running), the bytes cannot say, so the rows do: a group made only of the manager's own row
    /// is its self-update script, anything else is the managed one. That is the same name-based
    /// rule <see cref="PackageManagerCatalog.CurrentScriptFor"/> uses to decide which text to write.
    /// </remarks>
    private static string PackageManagerScriptLabel(string platform, string script, IReadOnlyList<UpgradePath> rows)
    {
        var managerName = PlatformBucket.PackageManagerNameFrom(platform)!;
        PackageManagerCatalog.TryGet(managerName, out var manager);

        var identity = ApprovedScriptIdentity.For(platform, script, rows[0].ApplicationName, null);
        if (identity.IsPackageManagerScript)
        {
            return identity.DisplayName;
        }

        var isSelfUpdate = rows.All(r => string.Equals(r.ApplicationName, manager.Name, StringComparison.OrdinalIgnoreCase));
        return ApprovedScriptIdentity.PackageManagerDisplayName(manager.Name, isSelfUpdate);
    }
}
