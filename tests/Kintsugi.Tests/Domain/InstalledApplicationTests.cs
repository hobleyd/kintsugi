using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class InstalledApplicationTests
{
    [Fact]
    public void UpdateVersion_SetsTheNewVersion()
    {
        var application = new InstalledApplication(Guid.NewGuid(), "Firefox", "128.0");

        application.UpdateVersion("129.0");

        Assert.Equal("129.0", application.Version);
    }

    /// <summary>A patch that just landed is the update the manager was reporting; leaving the
    /// verdict standing would keep the host counted as behind until its next inventory report.</summary>
    [Fact]
    public void UpdateVersion_ClearsThePackageManagersPendingUpdateVerdict()
    {
        var application = new InstalledApplication(Guid.NewGuid(), "Firefox", "128.0", updateAvailable: true);

        application.UpdateVersion("129.0");

        Assert.False(application.UpdateAvailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void UpdateVersion_RejectsAMissingVersion(string? version)
    {
        var application = new InstalledApplication(Guid.NewGuid(), "Firefox", "128.0");

        Assert.Throws<DomainException>(() => application.UpdateVersion(version!));
    }
}
