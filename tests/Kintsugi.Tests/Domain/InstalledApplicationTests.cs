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
