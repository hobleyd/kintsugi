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

public record AgentPackageSourceStatusRow(
    string Platform,
    string AvailableVersion,
    string? PublishedVersion,
    bool IsNewer);
