using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.PatchFailures;

/// <summary>
/// One (host, application) patch failure as the Failed Updates screen shows it — the reported
/// failure joined with the host that reported it and the upgrade path it is about.
/// </summary>
/// <param name="Platform">The upgrade path's platform bucket, resolved when the failure was
/// reported. Null when nothing resolved, which is what makes <see cref="CanFix"/> false.</param>
/// <param name="HasScript">Whether the upgrade path this failure names still holds a script. The
/// screen's fix panel edits and re-signs that script, so a row without one has nothing to fix
/// through the panel — the path needs researching first.</param>
/// <param name="ScriptSigned">Whether that script currently carries a human-approved signature.
/// Shown beside the failure because an unsigned script is one no agent will run at all, which
/// changes what "still failing" means for the row.</param>
/// <remarks>
/// Hand-mirrored into <c>web/lib/data/models/patch_failure_mapper.dart</c>; see
/// <c>.claude/rules/hand-mirrored-dtos.md</c>. Nothing in CI cross-checks the two.
/// </remarks>
public record PatchFailureDto(
    Guid Id,
    Guid HostId,
    string Hostname,
    string? SerialNumber,
    string ApplicationName,
    string? Platform,
    string? InstalledVersion,
    string? AttemptedVersion,
    string Details,
    DateTimeOffset FirstFailedUtc,
    DateTimeOffset LastFailedUtc,
    int FailureCount,
    PatchFailureResolution Resolution,
    DateTimeOffset? ResolvedUtc,
    bool HasScript,
    bool ScriptSigned,
    UpgradeMethod Method)
{
    /// <summary>
    /// Whether the screen can offer this row the fix flow at all — an AI repair or a hand edit both
    /// act on a stored <c>UpgradePath</c> row, so both need one to exist.
    /// </summary>
    /// <remarks>
    /// Serialized rather than derived client-side for the same reason
    /// <c>UpgradePathSummaryDto.StatusKey</c> is: the rule is a join the client cannot see, and a
    /// second copy of it in Dart would be free to disagree.
    /// </remarks>
    public bool CanFix => Platform is not null && HasScript;

    /// <summary>
    /// Whether this row is a failed operating-system update (Windows Update, macOS
    /// <c>softwareupdate</c>, a distribution's package manager) rather than an application. The
    /// three agents file those under the names <c>Windows</c>, <c>macOS</c> and <c>Linux</c>; see
    /// <see cref="OperatingSystemUpdateFailure"/>.
    /// </summary>
    public bool IsOperatingSystemUpdate => OperatingSystemUpdateFailure.IsOperatingSystem(ApplicationName);

    /// <summary>What to call the row on screen: "Windows Update" rather than a bare "Windows".
    /// Null for an application, which shows its own name.</summary>
    public string? DisplayName => OperatingSystemUpdateFailure.DisplayName(ApplicationName);

    /// <summary>Why an operating-system update failed, in a few words. Null for an application.</summary>
    public string? CauseTitle => OperatingSystemUpdateFailure.Explain(ApplicationName, Details)?.Title;

    /// <summary>A sentence saying why that happens. Null for an application.</summary>
    public string? CauseExplanation => OperatingSystemUpdateFailure.Explain(ApplicationName, Details)?.Explanation;

    /// <summary>What to do about it. Null for an application.</summary>
    public string? SuggestedFix => OperatingSystemUpdateFailure.Explain(ApplicationName, Details)?.SuggestedFix;
}
