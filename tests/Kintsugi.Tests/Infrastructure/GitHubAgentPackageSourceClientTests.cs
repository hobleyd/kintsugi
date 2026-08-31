using Kintsugi.Infrastructure.AgentPackages;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// Against a captured GitHub releases payload rather than a live call — the same reason each
/// agent's package-manager output parsers take a plain string and are tested against real captured
/// output. What breaks this parser in production is a shape change, and a live test would only
/// catch that once it had already shipped.
/// </summary>
public class GitHubAgentPackageSourceClientTests
{
    private static string Release(
        string tag,
        string assetName,
        bool draft = false,
        string body = "Build notes.") =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": false,
          "body": "{{body}}",
          "assets": [
            {
              "name": "{{assetName}}",
              "size": 4194304,
              "browser_download_url": "https://github.com/hobleyd/kintsugi/releases/download/{{tag}}/{{assetName}}"
            }
          ]
        }
        """;

    private static string Listing(params string[] releases) => $"[{string.Join(",", releases)}]";

    [Fact]
    public void ParseLatestReleases_ReadsOneReleasePerPlatform()
    {
        var json = Listing(
            Release("macos-agent-v0.5.0", "kintsugi-agent-macos-0.5.0.tar.gz"),
            Release("windows-agent-v0.5.0", "kintsugi-agent-windows-0.5.0.tar.gz"),
            Release("linux-agent-v0.5.0", "kintsugi-agent-linux-0.5.0.tar.gz"));

        var releases = GitHubAgentPackageSourceClient.ParseLatestReleases(json);

        Assert.Equal(new[] { "linux", "macos", "windows" }, releases.Select(r => r.Platform));
        Assert.All(releases, r => Assert.Equal("0.5.0", r.Version));
    }

    [Fact]
    public void ParseLatestReleases_CarriesTheAssetNameAndDownloadUrl()
    {
        var json = Listing(Release("macos-agent-v0.5.0", "kintsugi-agent-macos-0.5.0.tar.gz"));

        var release = Assert.Single(GitHubAgentPackageSourceClient.ParseLatestReleases(json));

        Assert.Equal("kintsugi-agent-macos-0.5.0.tar.gz", release.FileName);
        Assert.Equal(
            "https://github.com/hobleyd/kintsugi/releases/download/macos-agent-v0.5.0/kintsugi-agent-macos-0.5.0.tar.gz",
            release.DownloadUrl);
        Assert.Equal("Build notes.", release.ReleaseNotes);
    }

    [Fact]
    public void ParseLatestReleases_PicksTheHighestVersion_NotTheFirstListed()
    {
        // GitHub returns newest-created first, but "created most recently" and "highest version"
        // are not the same thing once a release is re-cut, and the version is what everything
        // downstream keys on.
        var json = Listing(
            Release("linux-agent-v0.4.9", "kintsugi-agent-linux-0.4.9.tar.gz"),
            Release("linux-agent-v0.10.0", "kintsugi-agent-linux-0.10.0.tar.gz"),
            Release("linux-agent-v0.9.0", "kintsugi-agent-linux-0.9.0.tar.gz"));

        var release = Assert.Single(GitHubAgentPackageSourceClient.ParseLatestReleases(json));

        Assert.Equal("0.10.0", release.Version);
    }

    [Fact]
    public void ParseLatestReleases_SkipsDrafts()
    {
        var json = Listing(
            Release("macos-agent-v0.6.0", "kintsugi-agent-macos-0.6.0.tar.gz", draft: true),
            Release("macos-agent-v0.5.0", "kintsugi-agent-macos-0.5.0.tar.gz"));

        var release = Assert.Single(GitHubAgentPackageSourceClient.ParseLatestReleases(json));

        Assert.Equal("0.5.0", release.Version);
    }

    [Fact]
    public void ParseLatestReleases_IgnoresReleasesThatArentAgentBuilds()
    {
        // This repository is free to publish releases of its own that have nothing to do with the
        // agents; a tag that doesn't match the shape ci.yml creates is not an error.
        var json = Listing(
            Release("v1.2.3", "kintsugi-server-1.2.3.tar.gz"),
            Release("macos-agent-v0.5.0", "kintsugi-agent-macos-0.5.0.tar.gz"));

        var release = Assert.Single(GitHubAgentPackageSourceClient.ParseLatestReleases(json));

        Assert.Equal("macos", release.Platform);
    }

    [Fact]
    public void ParseLatestReleases_IgnoresAReleaseWithNoTarGzAsset()
    {
        // A release whose build job failed part way can exist with no usable asset, and offering
        // it on the Clients page would only produce a refresh that fails at download time.
        var json = Listing(
            Release("macos-agent-v0.6.0", "checksums.txt"),
            Release("macos-agent-v0.5.0", "kintsugi-agent-macos-0.5.0.tar.gz"));

        var release = Assert.Single(GitHubAgentPackageSourceClient.ParseLatestReleases(json));

        Assert.Equal("0.5.0", release.Version);
    }

    [Fact]
    public void ParseLatestReleases_BlankBody_ComesBackAsNoReleaseNotes()
    {
        var json = Listing(Release("macos-agent-v0.5.0", "kintsugi-agent-macos-0.5.0.tar.gz", body: "   "));

        var release = Assert.Single(GitHubAgentPackageSourceClient.ParseLatestReleases(json));

        Assert.Null(release.ReleaseNotes);
    }

    [Fact]
    public void ParseLatestReleases_EmptyListing_ReturnsNothing() =>
        Assert.Empty(GitHubAgentPackageSourceClient.ParseLatestReleases("[]"));
}
