using MediatR;

namespace Kintsugi.Application.UpgradePaths.Commands.SignUpgradePathScript;

/// <summary>
/// Signs one already-saved upgrade path's <c>Script</c> with the server's artifact-signing key,
/// after a human has reviewed it — the only way an upgrade path's <c>ScriptSignature</c> gets set,
/// now that script signing is deliberately excluded from the automatic research/save flows (see
/// <c>ResearchApplicationUpgradePathCommandHandler</c> and <c>SaveUpgradePathCommandHandler</c>).
/// Backs the "Sign Script" action on the Applications page's per-row panel, which only appears
/// once a script is present and unsigned.
/// </summary>
public record SignUpgradePathScriptCommand(string ApplicationName, string Platform) : IRequest<UpgradePathResultDto>;
