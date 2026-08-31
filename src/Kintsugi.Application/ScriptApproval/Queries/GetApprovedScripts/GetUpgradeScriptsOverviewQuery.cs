using MediatR;

namespace Kintsugi.Application.ScriptApproval.Queries.GetApprovedScripts;

/// <summary>
/// Everything the Upgrade Scripts page renders, in one query — the upstream repository's state, the
/// approved corpus this server has imported, this server's own local scripts, and the adoption
/// candidates that pair the two.
/// </summary>
public record GetUpgradeScriptsOverviewQuery : IRequest<UpgradeScriptsOverviewDto>;

/// <param name="ThisServerFingerprint">This server's own signer fingerprint. Shown so an operator can
/// tell their own approvals from another server's at a glance, which matters because it is the one
/// fingerprint whose signatures are verifiable against a key that never left this host.</param>
/// <param name="PublishingEnabled">Whether a write token is configured. False means signing still
/// works locally but raises no pull request, which the page says out loud — an operator who expected
/// an audit trail should not have to discover its absence by looking for pull requests that were
/// never opened.</param>
public record UpgradeScriptsOverviewDto(
    string Repository,
    string? DefaultBranch,
    string? HeadCommitSha,
    string? UnavailableReason,
    bool PublishingEnabled,
    string ThisServerFingerprint,
    IReadOnlyList<ApprovedScriptDto> Approved,
    IReadOnlyList<LocalScriptDto> LocalScripts,
    IReadOnlyList<AdoptionCandidateDto> AdoptionCandidates)
{
    /// <summary>Local scripts a human here still has to review — the count the page leads with,
    /// because it is the only number on the page that represents work outstanding.</summary>
    public int AwaitingReview => LocalScripts.Count(s => !s.Signed);
}

/// <param name="IsThisServer">True when this server signed it.</param>
/// <param name="HeldLocally">True when some local upgrade path's script is byte-for-byte this
/// content — i.e. this approval is already doing work here rather than merely being known about.</param>
public record ApprovedScriptDto(
    string Sha256,
    string PlatformBucket,
    string ApplicationName,
    string SignerFingerprint,
    bool IsThisServer,
    string? SignedBy,
    DateTimeOffset ApprovedAtUtc,
    string SourceCommitSha,
    bool HeldLocally);

/// <param name="Signed">Whether a human's approval covers it, and so whether any agent will run it.</param>
/// <param name="ApprovedUpstream">Whether these exact bytes appear in the imported corpus. An unsigned
/// row that is approved upstream is a bug worth seeing — a refresh should have blessed it — and is
/// normally the language-mismatch case the import reports.</param>
public record LocalScriptDto(
    string ApplicationName,
    string Platform,
    string Sha256,
    bool Signed,
    bool ApprovedUpstream);

/// <param name="ApplicationName">The local row's application, not the approving server's note of
/// one — this is a row that exists here and has no approved script.</param>
public record AdoptionCandidateDto(
    string ApplicationName,
    string Platform,
    string Sha256,
    string SignerFingerprint,
    bool IsThisServer,
    string? SignedBy,
    DateTimeOffset ApprovedAtUtc,
    bool ReplacesExistingScript);
