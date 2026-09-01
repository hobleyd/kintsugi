namespace Kintsugi.Application.ScriptApproval;

/// <param name="Repository">Which repository is being read, for display.</param>
/// <param name="HeadCommitSha">The default branch's current head — the trust root's exact state.
/// Null when <paramref name="UnavailableReason"/> is set.</param>
/// <param name="UnavailableReason">Why the upstream couldn't be read, or null. Reported rather than
/// thrown, because this is checked on every Upgrade Scripts page load and an unreachable GitHub must
/// not take the page down with it — the same contract <c>GetAgentPackageSourceStatusQueryHandler</c>
/// has.</param>
public record ScriptApprovalSourceStatus(
    string Repository,
    string? DefaultBranch,
    string? HeadCommitSha,
    string? UnavailableReason);

/// <summary>
/// One content-addressed directory of the approval repository, as read back out of it.
/// </summary>
/// <param name="Sha256">The directory name, already checked against the script's actual hash.</param>
/// <param name="Signatures">Every signer's attestation found under <c>signatures/</c>. More than one
/// is normal and desirable — it means several servers' reviewers independently vouched for these
/// exact bytes.</param>
public record ApprovedScriptCorpusEntry(
    string Sha256,
    ApprovedScriptMetadataDocument Metadata,
    string Script,
    IReadOnlyList<ApprovedScriptSignatureDocument> Signatures);

/// <param name="Entries">Every well-formed entry found.</param>
/// <param name="SkippedReasons">Why anything else was passed over. Surfaced on the page rather than
/// swallowed: a corpus that silently drops half its entries looks exactly like a small corpus, and
/// the most likely cause — a hash that doesn't match its directory — is the one worth seeing.</param>
public record ApprovedScriptCorpusReadResult(
    IReadOnlyList<ApprovedScriptCorpusEntry> Entries,
    IReadOnlyList<string> SkippedReasons);

/// <summary>
/// Reads the shared approval repository's corpus of signed scripts — the counterpart to
/// <c>IScriptApprovalPublisher</c>, and the mechanism behind the Upgrade Scripts page's "Refresh
/// scripts" action.
/// </summary>
public interface IScriptApprovalSourceClient
{
    /// <summary>Cheap enough to run on every page load: a request for the default branch's head
    /// commit, on a short leash, with the branch name itself remembered after the first call.</summary>
    Task<ScriptApprovalSourceStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The whole corpus at <paramref name="commitish"/>, fetched in a single request.
    ///
    /// One request for everything rather than walking the git tree and fetching each blob: the corpus
    /// is a directory of small files, and a per-blob walk would spend one API call per file against a
    /// rate limit of 60/hour unauthenticated.
    /// </summary>
    Task<ApprovedScriptCorpusReadResult> GetCorpusAsync(string commitish, CancellationToken cancellationToken);
}
