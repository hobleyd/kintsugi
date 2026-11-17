using MediatR;

namespace Kintsugi.Application.UpgradePaths.Commands.SignUpgradePathScript;

/// <summary>
/// Signs one already-saved upgrade path's <c>Script</c> with the server's artifact-signing key,
/// after a human has reviewed it — the only way an upgrade path's <c>ScriptSignature</c> gets set,
/// now that script signing is deliberately excluded from the automatic research/save flows (see
/// <c>ResearchApplicationUpgradePathCommandHandler</c> and <c>SaveUpgradePathCommandHandler</c>).
/// Backs the "Sign Script" action on the Applications page's per-row panel, which only appears
/// once a script is present and unsigned.
///
/// Signing is effective immediately: the human at the console reviewed it, so agents may run it from
/// the next check-in. The approval is *also* published to the shared approval repository as a pull
/// request (see <c>IScriptApprovalPublisher</c>), which is what records the decision durably and lets
/// another server adopt it — a record of the approval, not a gate on it.
/// </summary>
/// <param name="ExpectedScriptSha256">Lowercase hex SHA-256 (see <c>ScriptContentHash.Of</c>) of the
/// script the reviewer was shown when they pressed Sign. Required: the handler refuses to sign unless
/// the stored script still hashes to exactly this, so a script regenerated or re-saved by somebody
/// else while the panel was open is never signed under a review of a different one.</param>
/// <param name="SignedBy">Who reviewed it, from the authenticated session — recorded in the published
/// approval entry. Null when the site is running with authentication disabled.</param>
/// <param name="PatchFailureId">Set when this signature is a *repair* driven from the Failed Updates
/// screen, which clears the failures it addresses (see the handler). Null for an ordinary signature
/// from the Applications screen, which clears nothing.</param>
public record SignUpgradePathScriptCommand(
    string ApplicationName, string Platform, string ExpectedScriptSha256, string? SignedBy = null, Guid? PatchFailureId = null)
    : IRequest<UpgradePathResultDto>;
