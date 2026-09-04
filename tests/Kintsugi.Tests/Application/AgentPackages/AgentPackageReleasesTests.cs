using Kintsugi.Application.AgentPackages;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Tests.Application.AgentPackages;

public class AgentPackageReleasesTests
{
    private static AgentPackageSourceRelease Release(string platform, string version, string? notes = null) =>
        new(platform, version, $"kintsugi-agent-{platform}-{version}.tar.gz", $"https://example/{platform}/{version}", notes);

    [Fact]
    public void LatestPerPlatform_ReadsOneReleasePerPlatform_OrderedByPlatform()
    {
        var releases = new[]
        {
            Release("macos", "0.5.0"),
            Release("windows", "0.5.0"),
            Release("linux", "0.5.0"),
        };

        var latest = AgentPackageReleases.LatestPerPlatform(releases);

        Assert.Equal(new[] { "linux", "macos", "windows" }, latest.Select(r => r.Platform));
        Assert.All(latest, r => Assert.Equal("0.5.0", r.Version));
    }

    [Fact]
    public void LatestPerPlatform_PicksTheHighestVersion_NotTheFirstListed()
    {
        // GitHub returns newest-created first, but "created most recently" and "highest version"
        // are not the same thing once a release is re-cut, and the version is what everything
        // downstream keys on.
        var releases = new[]
        {
            Release("linux", "0.4.9"),
            Release("linux", "0.10.0"),
            Release("linux", "0.9.0"),
        };

        Assert.Equal("0.10.0", Assert.Single(AgentPackageReleases.LatestPerPlatform(releases)).Version);
    }

    [Theory]
    [InlineData("0.5.0", "0.5.0-rc1")]
    [InlineData("0.5.0-rc1", "0.5.0")]
    public void LatestPerPlatform_UnorderableVersions_PickTheSameOneWhicheverOrderTheyreListedIn(
        string first, string second)
    {
        // "0.5.0" and "0.5.0-rc1" can't be ordered against each other, and the permissive
        // "different means newer" rule would answer yes in both directions — so whichever GitHub
        // listed second would win, and the selected build would depend on listing order rather
        // than on the versions. Selection uses IsHigherThan, which falls back to GitHub's
        // newest-created-first order instead.
        var releases = new[] { Release("macos", first), Release("macos", second) };

        Assert.Equal(first, Assert.Single(AgentPackageReleases.LatestPerPlatform(releases)).Version);
    }

    [Fact]
    public void NewerThan_ListsEveryBuildAbovePublished_HighestFirst_WithItsNotes()
    {
        // Listing order is newest-created first, which is not version order once a patch is
        // backported — 0.9.1 cut after 0.10.0 here — so the result is sorted by version, not kept.
        var releases = new[]
        {
            Release("linux", "0.9.1", "Backported fix."),
            Release("linux", "0.10.0", "Big one."),
            Release("macos", "0.10.0", "Other platform."),
            Release("linux", "0.9.0", "Already published."),
            Release("linux", "0.8.0", "Older still."),
        };

        var newer = AgentPackageReleases.NewerThan(releases, "linux", "0.9.0");

        Assert.Equal(new[] { ("0.10.0", "Big one."), ("0.9.1", "Backported fix.") }, newer.Select(r => (r.Version, r.ReleaseNotes!)));
    }

    [Fact]
    public void NewerThan_NothingPublished_ListsEveryBuild()
    {
        var releases = new[] { Release("linux", "0.5.0"), Release("linux", "0.6.0") };

        Assert.Equal(new[] { "0.6.0", "0.5.0" }, AgentPackageReleases.NewerThan(releases, "linux", null).Select(r => r.Version));
    }

    [Fact]
    public void NewerThan_UpToDate_IsEmpty() =>
        Assert.Empty(AgentPackageReleases.NewerThan(new[] { Release("linux", "0.5.0") }, "linux", "0.5.0"));

    [Fact]
    public void NewerThan_UnparseableVersions_AreStillShown_AfterTheParseableOnes()
    {
        // The permissive IsNewer reading, the same one behind the row's "Available" chip: a version
        // that can't be parsed is worth showing, and the cost of a false positive is a note read
        // rather than a build installed. It sorts below the ones that can be ordered, in listing order.
        var releases = new[]
        {
            Release("macos", "0.6.0-rc2"),
            Release("macos", "0.6.0-rc1"),
            Release("macos", "0.6.0"),
        };

        Assert.Equal(
            new[] { "0.6.0", "0.6.0-rc2", "0.6.0-rc1" },
            AgentPackageReleases.NewerThan(releases, "macos", "0.5.0").Select(r => r.Version));
    }
}
