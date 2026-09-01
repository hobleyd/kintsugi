namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// The upstream that client builds come from — the project's own public GitHub repository, whose
/// CI builds one release per agent per version (see <c>.github/workflows/ci.yml</c>).
///
/// CI deliberately cannot publish to a Kintsugi server: it has no route to one, and a server's
/// address is deployment detail that must not be committed. So the direction is reversed — the
/// server pulls, rewrites each archive's <c>api_base_url</c> to its own address, and republishes
/// locally. See <c>ImportAgentPackagesFromSourceCommandHandler</c>.
/// </summary>
public interface IAgentPackageSourceClient
{

    /// <summary>The newest release available for each platform, at most one per platform. A
    /// platform with no release at all is simply absent rather than an error — nothing has been
    /// built for it yet.</summary>
    Task<IReadOnlyList<AgentPackageSourceRelease>> GetLatestReleasesAsync(CancellationToken cancellationToken);

    /// <summary>Downloads one release's archive. The returned stream is positioned at 0 and owned
    /// by the caller.</summary>
    Task<Stream> DownloadAsync(AgentPackageSourceRelease release, CancellationToken cancellationToken);
}

/// <summary>
/// One platform's newest available build upstream. <paramref name="Platform"/> is the agent-package
/// namespace ("macos"/"windows"/"linux"), which is deliberately separate from
/// <c>PlatformBucket</c>'s upgrade-path buckets.
/// </summary>
public record AgentPackageSourceRelease(
    string Platform,
    string Version,
    string FileName,
    string DownloadUrl,
    string? ReleaseNotes);
