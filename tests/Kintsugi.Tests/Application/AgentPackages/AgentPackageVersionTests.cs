using Kintsugi.Application.AgentPackages;

namespace Kintsugi.Tests.Application.AgentPackages;

public class AgentPackageVersionTests
{
    [Theory]
    [InlineData("0.5.1", "0.5.0")]
    [InlineData("0.6.0", "0.5.9")]
    [InlineData("1.0.0", "0.99.99")]
    public void IsNewer_HigherVersion_IsNewer(string candidate, string current) =>
        Assert.True(AgentPackageVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("0.5.0", "0.5.0")]
    [InlineData("0.5.0", "0.5.1")]
    [InlineData("0.9.0", "0.10.0")]
    public void IsNewer_SameOrLowerVersion_IsNotNewer(string candidate, string current) =>
        Assert.False(AgentPackageVersion.IsNewer(candidate, current));

    [Fact]
    public void IsNewer_ComparesNumerically_NotAsText()
    {
        // The case an ordinal string compare gets backwards, and the reason this isn't just a
        // string inequality: "0.10.0" sorts before "0.9.0" as text.
        Assert.True(AgentPackageVersion.IsNewer("0.10.0", "0.9.0"));
        Assert.False(AgentPackageVersion.IsNewer("0.9.0", "0.10.0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsNewer_NothingPublishedYet_IsAlwaysNewer(string? current) =>
        Assert.True(AgentPackageVersion.IsNewer("0.5.0", current));

    [Fact]
    public void IsNewer_UnparseableVersion_FallsBackToDifferenceMeaningNewer()
    {
        // Deliberately permissive: a version this can't order is still worth surfacing on the
        // Clients page, and importing it is a no-op if it turns out to be the same build.
        Assert.True(AgentPackageVersion.IsNewer("0.5.0-rc1", "0.5.0"));
        Assert.False(AgentPackageVersion.IsNewer("0.5.0-rc1", "0.5.0-rc1"));
    }

    [Fact]
    public void IsNewer_IgnoresSurroundingWhitespace() =>
        Assert.False(AgentPackageVersion.IsNewer(" 0.5.0 ", "0.5.0"));
}
