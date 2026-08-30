using Moq;
using Kintsugi.Application.AgentPackages.Queries.GetAgentPackages;
using Kintsugi.Application.AgentPackages.Queries.GetLatestAgentPackage;
using Kintsugi.Application.Applications;
using Kintsugi.Application.Applications.Queries.GetApplicationSummaries;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Hosts.Queries.GetHostById;
using Kintsugi.Application.Hosts.Queries.GetHosts;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathSummaries;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application;

/// <summary>
/// Handlers whose entire job is delegating straight to a repository method with no branching of
/// their own — low bug surface individually, but worth a quick check that each one is wired to the
/// right repository call and DTO mapping, since a typo here (e.g. calling the wrong repository
/// method) wouldn't be caught by anything else.
/// </summary>
public class ThinQueryHandlerTests
{
    [Fact]
    public async Task GetHostsQueryHandler_MapsEveryHostToADto()
    {
        var hostA = new Host("host-1", "SERIAL-1");
        var hostB = new Host("host-2", "SERIAL-2");
        var repository = new Mock<IHostRepository>();
        repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { hostA, hostB });
        var upgradePathRepository = new Mock<IUpgradePathRepository>();
        upgradePathRepository.Setup(r => r.GetAppUpdateCountsByHostAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int>());

        var result = await new GetHostsQueryHandler(repository.Object, upgradePathRepository.Object)
            .Handle(new GetHostsQuery(), CancellationToken.None);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetHostsQueryHandler_PopulatesAppUpdatesAvailableCount_FromTheUpgradePathRepository()
    {
        var hostWithUpdates = new Host("host-1", "SERIAL-1");
        var hostUpToDate = new Host("host-2", "SERIAL-2");
        var repository = new Mock<IHostRepository>();
        repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { hostWithUpdates, hostUpToDate });
        var upgradePathRepository = new Mock<IUpgradePathRepository>();
        upgradePathRepository.Setup(r => r.GetAppUpdateCountsByHostAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int> { [hostWithUpdates.Id] = 3 });

        var result = await new GetHostsQueryHandler(repository.Object, upgradePathRepository.Object)
            .Handle(new GetHostsQuery(), CancellationToken.None);

        Assert.Equal(3, result.Single(h => h.Id == hostWithUpdates.Id).AppUpdatesAvailableCount);
        Assert.Equal(0, result.Single(h => h.Id == hostUpToDate.Id).AppUpdatesAvailableCount);
    }

    [Fact]
    public async Task GetHostByIdQueryHandler_ReturnsNull_WhenNotFound()
    {
        var repository = new Mock<IHostRepository>();
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        var result = await new GetHostByIdQueryHandler(repository.Object).Handle(new GetHostByIdQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetHostByIdQueryHandler_MapsAFoundHostToADto()
    {
        var host = new Host("host-1", "SERIAL-1", "macOS 15.0");
        var repository = new Mock<IHostRepository>();
        repository.Setup(r => r.GetByIdAsync(host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(host);

        var result = await new GetHostByIdQueryHandler(repository.Object).Handle(new GetHostByIdQuery(host.Id), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("host-1", result!.Hostname);
    }

    [Fact]
    public async Task GetApplicationSummariesQueryHandler_DelegatesToTheRepository()
    {
        var repository = new Mock<IInstalledApplicationRepository>();
        repository.Setup(r => r.GetSummariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ApplicationSummaryDto("Firefox", 3, Array.Empty<string>(), Array.Empty<ApplicationSummaryDto>()) });

        var result = await new GetApplicationSummariesQueryHandler(repository.Object).Handle(new GetApplicationSummariesQuery(), CancellationToken.None);

        Assert.Equal("Firefox", Assert.Single(result).Name);
    }

    [Fact]
    public async Task GetUpgradePathSummariesQueryHandler_DelegatesToTheRepository()
    {
        var repository = new Mock<IUpgradePathRepository>();
        repository.Setup(r => r.GetSummariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new UpgradePathSummaryDto("Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script, null, null, null, null, null, DateTimeOffset.UtcNow, 3, 1, 2, Array.Empty<string>()),
        });

        var result = await new GetUpgradePathSummariesQueryHandler(repository.Object).Handle(new GetUpgradePathSummariesQuery(), CancellationToken.None);

        Assert.Equal("Firefox", Assert.Single(result).ApplicationName);
    }

    [Fact]
    public async Task GetAgentPackagesQueryHandler_MapsEveryPackageToADto()
    {
        var package = AgentPackage.Create("macos", "0.2.0", "file.tar.gz", 1024, new string('a', 64), "sig", null);
        var repository = new Mock<IAgentPackageRepository>();
        repository.Setup(r => r.GetLatestPerPlatformAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { package });

        var result = await new GetAgentPackagesQueryHandler(repository.Object).Handle(new GetAgentPackagesQuery(), CancellationToken.None);

        Assert.Equal("macos", Assert.Single(result).Platform);
    }

    [Fact]
    public async Task GetLatestAgentPackageQueryHandler_ReturnsNull_WhenNoneHasBeenPublished()
    {
        var repository = new Mock<IAgentPackageRepository>();
        repository.Setup(r => r.GetLatestByPlatformAsync("macos", It.IsAny<CancellationToken>())).ReturnsAsync((AgentPackage?)null);

        var result = await new GetLatestAgentPackageQueryHandler(repository.Object).Handle(new GetLatestAgentPackageQuery("macos"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestAgentPackageQueryHandler_NormalizesThePlatformToLowercase()
    {
        var package = AgentPackage.Create("macos", "0.2.0", "file.tar.gz", 1024, new string('a', 64), "sig", null);
        var repository = new Mock<IAgentPackageRepository>();
        repository.Setup(r => r.GetLatestByPlatformAsync("macos", It.IsAny<CancellationToken>())).ReturnsAsync(package);

        var result = await new GetLatestAgentPackageQueryHandler(repository.Object).Handle(new GetLatestAgentPackageQuery("macOS"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("0.2.0", result!.Version);
    }
}
