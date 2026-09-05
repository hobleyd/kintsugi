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

    /// <summary>Rows this build would now write a different script for, counting only the signed
    /// ones. Separate from <see cref="AwaitingReview"/> because it is not the same kind of work:
    /// these rows are signed and patching normally, and taking the newer script is a choice rather
    /// than something outstanding.
    ///
    /// Signed only, because everything said about them turns on that. An <em>unsigned</em> row can
    /// differ from this build's script too, but it is not patching (no agent runs an unsigned
    /// script) and it does not stay that way: <c>RegisterApplicationsCommandHandler</c> rewrites an
    /// unsigned row from the builder on the next inventory report, and every package manager in the
    /// catalog reports a catalog version for every installed package rather than only outdated ones
    /// (see the macOS agent's <c>brew_installed_info</c>), so the report is not conditional on the
    /// package being out of date. Counting those here would put a number in front of an operator
    /// that resolves itself within the hour. The per-row flag still shows on them, which is the only
    /// route left for a row whose host has stopped reporting.</summary>
    public int NewerServerScripts => LocalScripts.Count(s => s.Signed && s.NewerServerScriptAvailable);
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

/// <summary>One script a human here has or has not approved.</summary>
/// <remarks>
/// One entry per <em>script</em>, not per row. An AI-researched row is one application's script and
/// appears as itself. A package-manager row is not: every application Homebrew (winget, Flatpak…)
/// manages holds the same bytes, one "Sign Script" covers all of them
/// (<c>FindExistingSignatureForScriptAsync</c>), and there is nothing per-application on such a row
/// for a reviewer to look at — so listing "firefox", "slack", "zoom"… under <c>pm:Homebrew</c> was a
/// list of the same script hundreds of times. Those rows collapse into one entry per (bucket,
/// content, signed-or-not), named the way the approval repository names the same script
/// (<see cref="ApprovedScriptIdentity.PackageManagerDisplayName"/>), with
/// <paramref name="Applications"/> saying how many rows it stands for. Content is part of the key
/// because a manager's bucket can legitimately hold two texts at once — rows signed against an
/// older revision of a <c>*UpgradeScript.Build</c> body beside rows written by this build — and the
/// reviewer's decision is per text. Signed-or-not is part of it so a set that is half signed shows
/// as two entries rather than as one with a made-up verdict.
/// </remarks>
/// <param name="ApplicationName">The application, for an AI-researched script; the manager's label
/// for a package-manager one. Same convention as <see cref="ApprovedScriptDto.ApplicationName"/>.</param>
/// <param name="Applications">How many upgrade-path rows hold exactly this entry. 1 for an
/// AI-researched script; for a package-manager script, the number of applications it patches.</param>
/// <param name="Signed">Whether a human's approval covers it, and so whether any agent will run it.</param>
/// <param name="ApprovedUpstream">Whether these exact bytes appear in the imported corpus. An unsigned
/// row that is approved upstream is a bug worth seeing — a refresh should have blessed it — and is
/// normally the language-mismatch case the import reports.</param>
/// <param name="NewerServerScriptAvailable">True when this is a package-manager script that is not
/// what this server's current build writes for those rows — i.e. one of the
/// <c>*UpgradeScript.Build</c> bodies has been edited since they got their content. Surfaced
/// because a *signed* row is deliberately never rewritten by a routine inventory report
/// (<c>RegisterApplicationsCommandHandler</c>), so without this it would go on running the older
/// text indefinitely with nothing to say a fix existed. Can also be true of an unsigned entry, which
/// is a transient state the next inventory report clears — see
/// <see cref="UpgradeScriptsOverviewDto.NewerServerScripts"/>, which counts only the signed ones for
/// that reason. Always false for an AI-researched script, which has no canonical current version to
/// differ from.</param>
public record LocalScriptDto(
    string ApplicationName,
    string Platform,
    string Sha256,
    int Applications,
    bool Signed,
    bool ApprovedUpstream,
    bool NewerServerScriptAvailable);

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
