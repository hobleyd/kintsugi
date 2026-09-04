using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AgentPackages;

/// <summary>
/// The two ways the Clients screen reads one upstream listing: the newest build per platform (what
/// a refresh imports), and every build newer than what is published here (whose release notes the
/// screen shows under each row). Both are pure functions over the list
/// <see cref="IAgentPackageSourceClient.GetReleasesAsync"/> returns, so the listing is fetched once
/// per request and neither reading costs a second GitHub call.
/// </summary>
public static class AgentPackageReleases
{
    /// <summary>
    /// Picks the newest release for each platform, ordered by platform for a stable render.
    ///
    /// GitHub returns newest-created first, but "created most recently" and "highest version" are
    /// not the same thing once a patch is backported or a release is re-cut, and the version is
    /// what everything downstream keys on. <see cref="AgentPackageVersion.IsHigherThan"/>, not
    /// <see cref="AgentPackageVersion.IsNewer"/>: only a provably higher version displaces the
    /// incumbent, so two versions that can't be ordered against each other (a pre-release tag
    /// beside its final release) fall back to the source's newest-created-first order instead of to
    /// whichever was listed second.
    /// </summary>
    public static IReadOnlyList<AgentPackageSourceRelease> LatestPerPlatform(IEnumerable<AgentPackageSourceRelease> releases)
    {
        var newestPerPlatform = new Dictionary<string, AgentPackageSourceRelease>(StringComparer.OrdinalIgnoreCase);

        foreach (var release in releases)
        {
            if (!newestPerPlatform.TryGetValue(release.Platform, out var incumbent)
                || AgentPackageVersion.IsHigherThan(release.Version, incumbent.Version))
            {
                newestPerPlatform[release.Platform] = release;
            }
        }

        return newestPerPlatform.Values
            .OrderBy(r => r.Platform, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every release of <paramref name="platform"/> that is newer than <paramref name="publishedVersion"/>,
    /// highest version first — the builds a host would move through between what is published here
    /// and what is upstream. Nothing published yet means every release qualifies.
    ///
    /// "Newer" is <see cref="AgentPackageVersion.IsNewer"/>'s permissive reading, the same one that
    /// decides the row's "Available" chip: a version that cannot be parsed is still worth showing
    /// beside the ones that can, and the cost of a false positive here is a note somebody reads
    /// rather than a build somebody installs. Versions that parse are ordered numerically so
    /// "0.10.0" sits above "0.9.0"; any that don't keep the source's own order at the bottom.
    /// </summary>
    public static IReadOnlyList<AgentPackageSourceRelease> NewerThan(
        IEnumerable<AgentPackageSourceRelease> releases,
        string platform,
        string? publishedVersion) =>
        releases
            .Where(r => string.Equals(r.Platform, platform, StringComparison.OrdinalIgnoreCase)
                && AgentPackageVersion.IsNewer(r.Version, publishedVersion))
            // OrderByDescending is stable and Comparer<Version>.Default sorts null lowest, so the
            // unparseable ones land last in listing order without a second key.
            .OrderByDescending(r => Version.TryParse(r.Version.Trim(), out var parsed) ? parsed : null)
            .ToList();
}
