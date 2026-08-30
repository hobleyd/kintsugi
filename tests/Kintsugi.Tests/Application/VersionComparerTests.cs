using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Tests.Application;

public class VersionComparerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void IsNewer_WithNoLatestVersionKnown_ReturnsFalse(string? latest)
    {
        Assert.False(VersionComparer.IsNewer(latest, "1.0.0"));
    }

    [Fact]
    public void IsNewer_IdenticalStrings_ReturnsFalse()
    {
        Assert.False(VersionComparer.IsNewer("1.2.3", "1.2.3"));
    }

    [Fact]
    public void IsNewer_IdenticalStrings_IsCaseInsensitive()
    {
        Assert.False(VersionComparer.IsNewer("MacOS Sequoia", "macos sequoia"));
    }

    [Theory]
    [InlineData("1.2.4", "1.2.3")]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("1.10.0", "1.9.0")] // numeric, not lexicographic, comparison per component
    public void IsNewer_ANumericallyGreaterVersion_ReturnsTrue(string latest, string installed)
    {
        Assert.True(VersionComparer.IsNewer(latest, installed));
    }

    [Theory]
    [InlineData("1.2.2", "1.2.3")]
    [InlineData("1.0.0", "2.0.0")]
    public void IsNewer_ANumericallyLesserVersion_ReturnsFalse(string latest, string installed)
    {
        Assert.False(VersionComparer.IsNewer(latest, installed));
    }

    [Fact]
    public void IsNewer_WhenNeitherStringHasAnyDigits_ReturnsTrue_BiasedTowardsReportingAPossibleUpdate()
    {
        Assert.True(VersionComparer.IsNewer("latest", "installed"));
    }

    [Fact]
    public void IsNewer_TrailingFreeTextQualifiers_AreIgnoredWhenTheNumericVersionIsIdentical()
    {
        // "Patch 1"'s digit shouldn't be treated as an extra version component.
        Assert.False(VersionComparer.IsNewer("2026.1.3 Patch 1", "2026.1.3"));
    }

    [Fact]
    public void IsNewer_HomebrewCaskRevisionSuffix_IsIgnored()
    {
        Assert.False(VersionComparer.IsNewer("2026.1.3,1", "2026.1.3"));
    }

    [Fact]
    public void IsNewer_ShorterVersionWithTrailingZerosImplied_ComparesCorrectly()
    {
        Assert.False(VersionComparer.IsNewer("1.2", "1.2.0"));
        Assert.True(VersionComparer.IsNewer("1.2.1", "1.2"));
    }
}
