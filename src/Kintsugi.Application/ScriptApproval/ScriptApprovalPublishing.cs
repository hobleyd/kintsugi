using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Application.ScriptApproval;

/// <summary>
/// One human-approved script, packaged for publication to the shared approval repository. Assembled
/// by <c>SignUpgradePathScriptCommandHandler</c> from the row it has just signed — over exactly the
/// bytes that were signed and persisted, never over editor content, for the same reason the signing
/// itself works that way.
/// </summary>
public record ScriptApprovalSubmission(
    string Sha256,
    string PlatformBucket,
    ScriptLanguage Language,
    string Script,
    string ApplicationName,
    string? ApplicationIdentifier,
    string SignerFingerprint,
    string SignerPublicKeyPem,
    string Signature,
    string? SignedBy,
    DateTimeOffset SignedAtUtc);

public enum ScriptApprovalPublishOutcome
{
    /// <summary>No write token is configured, so there is nowhere to publish. Reported rather than
    /// silently skipped: an operator who expected a pull request needs to know why there isn't one,
    /// and the local approval itself succeeded regardless.</summary>
    Disabled,

    /// <summary>A pull request was opened carrying this approval.</summary>
    PullRequestOpened,

    /// <summary>A pull request proposing this exact approval was already open — a second signature
    /// of the same content by the same signer, typically after a re-review.</summary>
    PullRequestAlreadyOpen,

    /// <summary>This signer's attestation over these bytes is already on the default branch, so
    /// there is nothing to propose.</summary>
    AlreadyApproved,

    /// <summary>Publication failed. The local approval still stands — see
    /// <c>SignUpgradePathScriptCommandHandler</c> on why this is deliberately not fatal.</summary>
    Failed,
}

/// <param name="PullRequestUrl">The pull request to review, when there is one — surfaced on the
/// Applications page's panel so whoever just signed can go and get it merged.</param>
public record ScriptApprovalPublishResult(
    ScriptApprovalPublishOutcome Outcome,
    string? PullRequestUrl = null,
    string? Message = null);

/// <summary>
/// Publishes a human's script approval to the shared approval repository as a pull request, which is
/// how an approval becomes both an audit record and something another server can pick up (see
/// <c>IScriptApprovalSourceClient</c> for the other half).
/// </summary>
/// <remarks>
/// Deliberately not a gate. Signing a script is effective on this server the moment it is saved —
/// the human at the console reviewed it — and the pull request records and distributes that decision
/// afterwards. So every failure mode here is reported, never thrown into the signing path: a GitHub
/// outage must not be able to stop a reviewed script from patching the fleet it was reviewed for.
/// </remarks>
public interface IScriptApprovalPublisher
{
    /// <summary>Reports <see cref="ScriptApprovalPublishOutcome.Disabled"/> when no write token is
    /// configured on the GitHub settings page.</summary>
    Task<ScriptApprovalPublishResult> PublishAsync(ScriptApprovalSubmission submission, CancellationToken cancellationToken);
}

// Which repository this publishes to, and whether it can publish at all, deliberately are not
// properties here. They are configuration, not behaviour, and they are now editable at runtime — a
// synchronous property would have to have captured them at construction, which is exactly the bug
// moving these settings into the database introduced. Callers that need to display them read
// IGitHubSettingsProvider, which is where every other consumer gets them too.
