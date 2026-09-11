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

/// <param name="Success">True only when the script ran and reported a version.</param>
/// <param name="Skipped">
/// True when there was nothing to run — the row has no script, or no
/// <c>ApplicationIdentifier</c> to pass one. Distinct from an unsuccessful check, because a row
/// this command declined to check has not failed at anything: the fleet-wide run counts the two
/// separately (<see cref="UpdateCheckStatusDto"/>), the same way the scan separates "already
/// known" from "failed". A skipped result is never <see cref="Success"/>.
/// </param>
public record CheckApplicationUpdateResult(
    string ApplicationName,
    string Platform,
    bool Success,
    bool VersionChanged,
    string? Note,
    bool Skipped = false);
