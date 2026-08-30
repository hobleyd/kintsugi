using MediatR;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathPrompt;

/// <summary>
/// The default AI prompt that would be sent to research one application's upgrade path — shown in
/// the Applications page's per-row instructions panel so a user can review or hand-edit it before
/// it's actually sent, via <c>RefreshUpgradePathCommand</c>'s prompt override.
/// </summary>
public record GetUpgradePathPromptQuery(string ApplicationName, string? Platform) : IRequest<UpgradePathPromptDto>;

/// <param name="Platform">The platform the prompt was (or would be) built for — resolved
/// server-side (the first match, ordered by platform name) when the request didn't specify one.
/// Null only when nothing installed matches <see cref="GetUpgradePathPromptQuery.ApplicationName"/> at all.</param>
/// <param name="Reason">Why no prompt is available, when <see cref="Available"/> is false — e.g.
/// the AI agent isn't configured, the application isn't currently installed anywhere, or it's
/// resolved deterministically via a package manager and never goes through the AI.</param>
/// <param name="ExistingResult">The already-resolved upgrade path for this application and
/// platform, when one exists — shown by default in the panel's Update Script box so a successful
/// check doesn't look empty just because it hasn't been re-sent to the AI this page load.</param>
public record UpgradePathPromptDto(bool Available, string? Platform, string? Prompt, string? Reason, UpgradePathResultDto? ExistingResult = null);
