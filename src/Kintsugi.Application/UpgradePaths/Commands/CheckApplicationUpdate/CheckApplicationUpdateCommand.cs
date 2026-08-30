using MediatR;

namespace Kintsugi.Application.UpgradePaths.Commands.CheckApplicationUpdate;

/// <summary>
/// Re-checks one already-resolved script upgrade path by running its own <c>--update-version</c>
/// mode — no AI call. The single unit of work a "Check for Updates" run fans out across every
/// (application, platform) currently resolved via a script. Never throws: any failure is caught
/// and reported back as an unsuccessful result rather than propagated, so one broken script can't
/// take down the rest of a run.
/// </summary>
public record CheckApplicationUpdateCommand(string ApplicationName, string Platform) : IRequest<CheckApplicationUpdateResult>;

public record CheckApplicationUpdateResult(
    string ApplicationName,
    string Platform,
    bool Success,
    bool VersionChanged,
    string? Note);
