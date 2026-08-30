using MediatR;

namespace Kintsugi.Application.UpgradePaths.Commands.StartUpdateCheck;

/// <summary>Requests that the background update checker start a run over every already-resolved
/// script upgrade path, re-running each one's own <c>--update-version</c> mode — no AI call.
/// Returns immediately — it does not wait for the run to finish. Backs the "Check for Updates"
/// button.</summary>
public record StartUpdateCheckCommand : IRequest<StartUpdateCheckResult>;

public record StartUpdateCheckResult(bool Started, UpdateCheckStatusDto Status);
