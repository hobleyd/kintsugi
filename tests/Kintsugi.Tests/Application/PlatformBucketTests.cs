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
        Assert.Equal("Windows", PlatformBucket.From(operatingSystem));
    }

    [Theory]
    [InlineData("Ubuntu 24.04")]
    [InlineData("Debian GNU/Linux 12")]
    [InlineData("CentOS Stream 9")]
    [InlineData("Fedora 40")]
    [InlineData("Linux 6.8")]
    public void From_LinuxDistributions_ReturnsLinux(string operatingSystem)
    {
        Assert.Equal("Linux", PlatformBucket.From(operatingSystem));
    }
}
