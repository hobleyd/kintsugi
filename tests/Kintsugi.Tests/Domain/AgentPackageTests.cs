using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class AgentPackageTests
{
    private static AgentPackage CreateValid() =>
        AgentPackage.Create("macos", "0.2.0", "kintsugi-agent-macos-0.2.0.tar.gz", 1024, new string('a', 64), "signature", "Fixes self-update.");

    [Fact]
    public void Create_WithValidFields_PopulatesEveryProperty()
    {
        var package = CreateValid();

        Assert.Equal("macos", package.Platform);
        Assert.Equal("0.2.0", package.Version);
        Assert.Equal("kintsugi-agent-macos-0.2.0.tar.gz", package.FileName);
        Assert.Equal(1024, package.FileSizeBytes);
        Assert.Equal(new string('a', 64), package.Sha256);
        Assert.Equal("signature", package.Sha256Signature);
        Assert.Equal("Fixes self-update.", package.ReleaseNotes);
    }

    [Fact]
    public void Create_WithNoReleaseNotes_LeavesThemNull()
    {
        var package = AgentPackage.Create("macos", "0.2.0", "file.tar.gz", 1024, new string('a', 64), "sig", null);

        Assert.Null(package.ReleaseNotes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_WithoutAPlatform_Throws(string? platform)
    {
        Assert.Throws<DomainException>(() =>
            AgentPackage.Create(platform!, "0.2.0", "file.tar.gz", 1024, new string('a', 64), "sig", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Create_WithoutAVersion_Throws(string? version)
    {
        Assert.Throws<DomainException>(() =>
            AgentPackage.Create("macos", version!, "file.tar.gz", 1024, new string('a', 64), "sig", null));
    }

    [Fact]
    public void Create_WithZeroFileSize_Throws()
    {
        Assert.Throws<DomainException>(() =>
            AgentPackage.Create("macos", "0.2.0", "file.tar.gz", 0, new string('a', 64), "sig", null));
    }

    [Fact]
    public void Create_WithoutAChecksum_Throws()
    {
        Assert.Throws<DomainException>(() =>
            AgentPackage.Create("macos", "0.2.0", "file.tar.gz", 1024, "", "sig", null));
    }

    [Fact]
    public void Create_WithoutASignature_Throws()
    {
        Assert.Throws<DomainException>(() =>
            AgentPackage.Create("macos", "0.2.0", "file.tar.gz", 1024, new string('a', 64), "", null));
    }
}
