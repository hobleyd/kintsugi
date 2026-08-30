using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths.Commands.RefreshUpgradePath;

/// <summary>
/// Re-checks the upgrade path for one application — either a single platform, or (when
/// <see cref="Platform"/> is <c>null</c>, as for a row that hasn't been checked at all yet) every
/// platform it's installed on. Runs synchronously and forces a fresh check even if the existing
/// path is already <see cref="Domain.Enums.UpgradePathStatus.Found"/>, unlike a fleet-wide scan
/// which skips those. Backs the per-row refresh action on the Applications page.
/// </summary>
/// <param name="PromptOverride">Hand-edited prompt text from the Applications page's per-row
/// instructions panel, sent to the AI verbatim in place of the default prompt. Ignored for
/// anything resolved via a package manager, which never calls the AI at all.</param>
public record RefreshUpgradePathCommand(string ApplicationName, string? Platform, string? PromptOverride = null) : IRequest<RefreshUpgradePathResult>;

public record RefreshUpgradePathResult(bool Success, string? ErrorMessage, IReadOnlyList<RefreshedUpgradePathDto>? Results = null);

/// <summary>One item's freshly-researched upgrade path, returned inline so the Applications page's
/// per-row panel can show what the check produced without a full page reload.</summary>
public record RefreshedUpgradePathDto(
    string ApplicationName,
    string Platform,
    UpgradePathStatus Status,
    string? LatestVersion,
    UpgradeMethod Method,
    string? DownloadUrl,
    string? Command,
    string? Instructions,
    string? SourceUrl,
    string? Notes,
    DateTimeOffset CheckedUtc,
    string? Script = null);
