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
    /// <summary>Every agent release upstream, all platforms and all versions, in the order the
    /// source lists them (GitHub: newest-created first). A platform with no release at all is
    /// simply absent rather than an error — nothing has been built for it yet.
    ///
    /// Every version rather than the newest per platform, because the Clients screen shows what a
    /// host would pick up between the version published here and the newest one upstream — the
    /// release notes of each build in between, not only the last. Callers that want one build per
    /// platform (the import) narrow it with <c>AgentPackageReleases.LatestPerPlatform</c>, so the
    /// listing is fetched once and read two ways.</summary>
    Task<IReadOnlyList<AgentPackageSourceRelease>> GetReleasesAsync(CancellationToken cancellationToken);

    /// <summary>Downloads one release's archive. The returned stream is positioned at 0 and owned
    /// by the caller.</summary>
    Task<Stream> DownloadAsync(AgentPackageSourceRelease release, CancellationToken cancellationToken);
}

/// <summary>
/// One agent build upstream. <paramref name="Platform"/> is the agent-package namespace
/// ("macos"/"windows"/"linux"), which is deliberately separate from <c>PlatformBucket</c>'s
/// upgrade-path buckets.
/// </summary>
public record AgentPackageSourceRelease(
    string Platform,
    string Version,
    string FileName,
    string DownloadUrl,
    string? ReleaseNotes);
