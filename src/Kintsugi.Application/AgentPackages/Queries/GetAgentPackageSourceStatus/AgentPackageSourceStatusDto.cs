namespace Kintsugi.Application.AgentPackages.Queries.GetAgentPackageSourceStatus;

/// <summary>
/// The Clients page's view of the upstream repository. <see cref="UnavailableReason"/> being set
/// is an ordinary outcome, not an exception the page has to handle: GitHub being unreachable must
/// leave the already-published packages listed and downloadable, so the failure is reported as
/// data rather than thrown.
/// </summary>
public record AgentPackageSourceStatusDto(
    string SourceDescription,
    IReadOnlyList<AgentPackageSourceStatusRow> Platforms,
    string? UnavailableReason)
{
    /// <summary>True when at least one platform has a build upstream that isn't published here
    /// yet — what turns the "Refresh clients" prompt on.</summary>
    public bool HasNewVersions => Platforms.Any(p => p.IsNewer);
}

/// <param name="NewerReleases">Every upstream build newer than <paramref name="PublishedVersion"/>,
/// highest first, with its release notes — what the Clients screen shows when a row is expanded.
/// Empty when the platform is up to date. <paramref name="AvailableVersion"/> is always its first
/// entry whenever <paramref name="IsNewer"/> is true.</param>
public record AgentPackageSourceStatusRow(
    string Platform,
    string AvailableVersion,
    string? PublishedVersion,
    bool IsNewer,
    IReadOnlyList<AgentPackageReleaseNotesDto> NewerReleases);

/// <summary>One upstream build's release notes, as GitHub holds them. Not truncated the way
/// <c>ImportAgentPackagesFromSourceCommandHandler</c> truncates the copy it stores: nothing here
/// is persisted, and a note cut off mid-sentence is worse than a long one on a screen that exists
/// to show it.</summary>
public record AgentPackageReleaseNotesDto(string Version, string? ReleaseNotes);
