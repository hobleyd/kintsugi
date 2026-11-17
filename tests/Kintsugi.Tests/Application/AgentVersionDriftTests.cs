using Kintsugi.Application.Hosts;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application;

public class AgentVersionDriftTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static Dictionary<string, PublishedAgent> Latest(string platform, string version, TimeSpan age) =>
        new(StringComparer.OrdinalIgnoreCase) { [platform] = new PublishedAgent(version, Now - age) };

    private static Host OnlineHost(string os, string? agentVersion)
    {
        var host = new Host("host", "SERIAL", os, agentVersion: agentVersion);
        host.RecordHeartbeat(HostStatus.Online);
        return host;
    }

    [Fact]
    public void AHostOnTheLatestAgentIsCurrent()
    {
        var (state, latest) = AgentVersionDrift.Evaluate(
            OnlineHost("Windows 11 Pro", "0.12.5"), Latest("windows", "0.12.5", TimeSpan.FromDays(3)), Now);

        Assert.Equal(AgentUpdateState.Current, state);
        Assert.Equal("0.12.5", latest);
    }

    [Fact]
    public void AHostOnANewerAgentThanPublishedIsCurrentNotBehind()
    {
        var (state, _) = AgentVersionDrift.Evaluate(
            OnlineHost("Windows 11 Pro", "0.12.9"), Latest("windows", "0.12.5", TimeSpan.FromDays(3)), Now);

        Assert.Equal(AgentUpdateState.Current, state);
    }

    [Fact]
    public void AHostBehindAPackagePublishedHoursAgoIsBehindNotStuck()
    {
        var (state, latest) = AgentVersionDrift.Evaluate(
            OnlineHost("Windows 11 Pro", "0.12.3"), Latest("windows", "0.12.5", TimeSpan.FromHours(2)), Now);

        Assert.Equal(AgentUpdateState.Behind, state);
        Assert.Equal("0.12.5", latest);
    }

    [Fact]
    public void AnOnlineHostStillBehindADayAfterPublicationIsNotUpdating()
    {
        var (state, _) = AgentVersionDrift.Evaluate(
            OnlineHost("Windows 11 Pro", "0.12.3"), Latest("windows", "0.12.5", TimeSpan.FromHours(25)), Now);

        Assert.Equal(AgentUpdateState.NotUpdating, state);
    }

    [Fact]
    public void AnOfflineHostIsNeverCalledStuckHoweverOldThePackage()
    {
        var host = new Host("host", "SERIAL", "Windows 11 Pro", agentVersion: "0.12.3");
        // Never heartbeated, so it is not online: it updates when it next checks in.

        var (state, _) = AgentVersionDrift.Evaluate(host, Latest("windows", "0.12.5", TimeSpan.FromDays(30)), Now);

        Assert.Equal(AgentUpdateState.Behind, state);
    }

    [Fact]
    public void VersionsAreComparedWithinAPlatformNeverAcrossThem()
    {
        // A Linux agent on 0.13.1 against a macOS agent published as 0.17.3 is not behind anything.
        var latest = new Dictionary<string, PublishedAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["macos"] = new PublishedAgent("0.17.3", Now - TimeSpan.FromDays(9)),
            ["linux"] = new PublishedAgent("0.13.1", Now - TimeSpan.FromDays(9))
        };

        var (state, version) = AgentVersionDrift.Evaluate(OnlineHost("Pop!_OS 22.04 LTS (Linux)", "0.13.1"), latest, Now);

        Assert.Equal(AgentUpdateState.Current, state);
        Assert.Equal("0.13.1", version);
    }

    [Theory]
    [InlineData("macOS 15.0", "macos")]
    [InlineData("Windows 11 Pro", "windows")]
    [InlineData("Ubuntu 24.04", "linux")]
    [InlineData("Pop!_OS 22.04 LTS (Linux)", "linux")]
    public void TheOperatingSystemChoosesThePackagePlatform(string os, string platform) =>
        Assert.Equal(platform, AgentVersionDrift.AgentPlatform(os));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FreeBSD 14")]
    public void AnOperatingSystemNoAgentRunsOnHasNoPlatform(string? os) =>
        Assert.Null(AgentVersionDrift.AgentPlatform(os));

    [Fact]
    public void AHostThatNeverReportedAVersionIsUnknownButStillKnowsTheLatest()
    {
        var (state, latest) = AgentVersionDrift.Evaluate(
            OnlineHost("Windows 11 Pro", null), Latest("windows", "0.12.5", TimeSpan.FromDays(3)), Now);

        Assert.Equal(AgentUpdateState.Unknown, state);
        Assert.Equal("0.12.5", latest);
    }

    [Fact]
    public void WhenNothingIsPublishedForThePlatformThereIsNothingToCompareWith()
    {
        var (state, latest) = AgentVersionDrift.Evaluate(
            OnlineHost("Windows 11 Pro", "0.12.3"), Latest("macos", "0.17.3", TimeSpan.FromDays(3)), Now);

        Assert.Equal(AgentUpdateState.Unknown, state);
        Assert.Null(latest);
    }
}
