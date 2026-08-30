using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Tests.Application;

public class PlatformBucketTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("FreeBSD 14")]
    public void From_UnknownOrMissingOperatingSystem_ReturnsGeneric(string? operatingSystem)
    {
        Assert.Equal(PlatformBucket.Generic, PlatformBucket.From(operatingSystem));
    }

    [Theory]
    [InlineData("macOS 15.0")]
    [InlineData("Mac OS X 10.15")]
    [InlineData("Darwin 24.0")]
    public void From_AppleOperatingSystems_ReturnsMacOs(string operatingSystem)
    {
        Assert.Equal(PlatformBucket.MacOs, PlatformBucket.From(operatingSystem));
    }

    [Theory]
    [InlineData("Windows 11")]
    [InlineData("Windows Server 2022")]
    public void From_WindowsOperatingSystems_ReturnsWindows(string operatingSystem)
    {
        Assert.Equal(PlatformBucket.Windows, PlatformBucket.From(operatingSystem));
    }

    [Theory]
    [InlineData("Ubuntu 24.04")]
    [InlineData("Debian GNU/Linux 12")]
    [InlineData("CentOS Stream 9")]
    [InlineData("Fedora 40")]
    [InlineData("Linux 6.8")]
    public void From_LinuxDistributions_ReturnsLinux(string operatingSystem)
    {
        Assert.Equal(PlatformBucket.Linux, PlatformBucket.From(operatingSystem));
    }

    [Fact]
    public void ForPackageManager_ProducesADistinctBucketPerManager()
    {
        // The property the whole split rests on: no two managers can ever share a bucket, so a row
        // written for one can never be inherited by an installation the other manages.
        Assert.NotEqual(
            PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew),
            PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget));
        Assert.NotEqual(
            PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget),
            PlatformBucket.ForPackageManager(PackageManagerCatalog.Chocolatey));
    }

    [Fact]
    public void ForPackageManager_IsNeverAnOsBucket()
    {
        // Otherwise a manager's rows could be reached by the (name, OS) lookup that runs first, and
        // a host would inherit them without that manager being installed at all.
        foreach (var manager in new[] { PackageManagerCatalog.Homebrew, PackageManagerCatalog.Winget, PackageManagerCatalog.Chocolatey })
        {
            var bucket = PlatformBucket.ForPackageManager(manager);
            Assert.True(PlatformBucket.IsPackageManagerBucket(bucket));
            Assert.NotEqual(PlatformBucket.MacOs, bucket);
            Assert.NotEqual(PlatformBucket.Windows, bucket);
            Assert.NotEqual(PlatformBucket.Linux, bucket);
            Assert.NotEqual(PlatformBucket.Generic, bucket);
        }
    }

    [Theory]
    [InlineData("winget")]
    [InlineData("WinGet")]
    [InlineData("WINGET")]
    public void ForPackageManager_NormalizesCasing_ForARecognizedManager(string reportedName)
    {
        // Two hosts spelling a manager differently must land on one row, not two — the bucket ends
        // up in a database column whose uniqueness constraint is case-sensitive.
        Assert.Equal(
            PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget),
            PlatformBucket.ForPackageManager(reportedName));
    }

    [Fact]
    public void PackageManagerNameFrom_RoundTripsAManagerBucket_AndIsNullForAnOsBucket()
    {
        Assert.Equal(
            PackageManagerCatalog.Homebrew,
            PlatformBucket.PackageManagerNameFrom(PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew)));
        Assert.Null(PlatformBucket.PackageManagerNameFrom(PlatformBucket.Windows));
    }

    [Fact]
    public void PackageManagerBuckets_FitTheDatabaseColumn()
    {
        // upgrade_paths.Platform is capped at 32 characters (see UpgradePathConfiguration) — a
        // longer bucket would fail on insert, not at compile time.
        foreach (var manager in new[] { PackageManagerCatalog.Homebrew, PackageManagerCatalog.Winget, PackageManagerCatalog.Chocolatey })
        {
            Assert.True(PlatformBucket.ForPackageManager(manager).Length <= 32);
        }
    }
}
