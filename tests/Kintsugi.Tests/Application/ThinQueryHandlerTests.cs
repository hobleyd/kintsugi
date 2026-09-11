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
        upgradePathRepository.Setup(r => r.GetInstallationPatchStatusesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<InstallationPatchStatus>());
        var vulnerabilityRepository = new Mock<IVulnerabilityRepository>();
        vulnerabilityRepository.Setup(r => r.GetApplicationCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ApplicationCveMatch>());
        vulnerabilityRepository.Setup(r => r.GetOperatingSystemCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OperatingSystemCveMatch>());
        vulnerabilityRepository.Setup(r => r.GetPackageCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PackageCveMatch>());
        var installedPackageRepository = new Mock<IInstalledPackageRepository>();

        var result = await new GetHostsQueryHandler(
                repository.Object, upgradePathRepository.Object, vulnerabilityRepository.Object, installedPackageRepository.Object)
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
        upgradePathRepository.Setup(r => r.GetInstallationPatchStatusesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new InstallationPatchStatus(hostWithUpdates.Id, "Chrome", "119.0", UpdateAvailable: true),
                new InstallationPatchStatus(hostWithUpdates.Id, "Firefox", "130.0", UpdateAvailable: true),
                new InstallationPatchStatus(hostWithUpdates.Id, "Slack", "4.0", UpdateAvailable: true),
                new InstallationPatchStatus(hostUpToDate.Id, "Chrome", "120.0", UpdateAvailable: false),
            });
        var vulnerabilityRepository = new Mock<IVulnerabilityRepository>();
        vulnerabilityRepository.Setup(r => r.GetApplicationCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ApplicationCveMatch>());
        vulnerabilityRepository.Setup(r => r.GetOperatingSystemCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OperatingSystemCveMatch>());
        vulnerabilityRepository.Setup(r => r.GetPackageCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PackageCveMatch>());
        var installedPackageRepository = new Mock<IInstalledPackageRepository>();

        var result = await new GetHostsQueryHandler(
                repository.Object, upgradePathRepository.Object, vulnerabilityRepository.Object, installedPackageRepository.Object)
            .Handle(new GetHostsQuery(), CancellationToken.None);

        Assert.Equal(3, result.Single(h => h.Id == hostWithUpdates.Id).AppUpdatesAvailableCount);
        Assert.Equal(0, result.Single(h => h.Id == hostUpToDate.Id).AppUpdatesAvailableCount);
    }

    [Fact]
    public async Task GetHostsQueryHandler_SplitsApplicationCveCounts_TreatingNoResolvedPathAsUnpatched()
    {
        var host = new Host("host-1", "SERIAL-1");
        var repository = new Mock<IHostRepository>();
        repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { host });
        var upgradePathRepository = new Mock<IUpgradePathRepository>();
        upgradePathRepository.Setup(r => r.GetInstallationPatchStatusesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                // Two Chromium-based browsers, both behind, sharing CVE-2024-0001.
                new InstallationPatchStatus(host.Id, "Chrome", "120.0", UpdateAvailable: true),
                new InstallationPatchStatus(host.Id, "Edge", "120.0", UpdateAvailable: true),
                // A current application whose latest version is still affected (a zero-day).
                new InstallationPatchStatus(host.Id, "Firefox", "130.0", UpdateAvailable: false),
                // No resolved upgrade path — nothing has researched it, so its CVE counts as
                // unpatched rather than being dropped as a coverage gap.
                new InstallationPatchStatus(host.Id, "Widget", "1.0", UpdateAvailable: null),
            });
        var vulnerabilityRepository = new Mock<IVulnerabilityRepository>();
        vulnerabilityRepository.Setup(r => r.GetApplicationCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ApplicationCveMatch("chrome", "120.0", "CVE-2024-0001"),
                new ApplicationCveMatch("edge", "120.0", "CVE-2024-0001"),
                new ApplicationCveMatch("firefox", "130.0", "CVE-2025-0002"),
                new ApplicationCveMatch("widget", "1.0", "CVE-2026-0003"),
            });
        vulnerabilityRepository.Setup(r => r.GetOperatingSystemCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OperatingSystemCveMatch>());
        vulnerabilityRepository.Setup(r => r.GetPackageCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PackageCveMatch>());
        var installedPackageRepository = new Mock<IInstalledPackageRepository>();

        var result = await new GetHostsQueryHandler(
                repository.Object, upgradePathRepository.Object, vulnerabilityRepository.Object, installedPackageRepository.Object)
            .Handle(new GetHostsQuery(), CancellationToken.None);

        var dto = Assert.Single(result);
        // CVE-2024-0001 (behind) and CVE-2026-0003 (no resolved path) — two distinct CVEs, not
        // three rows, even though CVE-2024-0001 came from two applications.
        Assert.Equal(2, dto.UnpatchedCveCount);
        Assert.Equal(1, dto.PatchedCveCount);
    }

    [Fact]
    public async Task GetHostsQueryHandler_SplitsOperatingSystemCveCounts_TreatingNeverCheckedAsUnpatched()
    {
        var hostBehind = new Host("host-1", "SERIAL-1", "macOS 14.5", operatingSystemUpdateAvailable: true);
        var hostCurrent = new Host("host-2", "SERIAL-2", "macOS 14.5", operatingSystemUpdateAvailable: false);
        var hostUnknown = new Host("host-3", "SERIAL-3", "macOS 14.5");
        var repository = new Mock<IHostRepository>();
        repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { hostBehind, hostCurrent, hostUnknown });
        var upgradePathRepository = new Mock<IUpgradePathRepository>();
        upgradePathRepository.Setup(r => r.GetInstallationPatchStatusesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<InstallationPatchStatus>());
        var vulnerabilityRepository = new Mock<IVulnerabilityRepository>();
        vulnerabilityRepository.Setup(r => r.GetApplicationCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ApplicationCveMatch>());
        vulnerabilityRepository.Setup(r => r.GetOperatingSystemCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new OperatingSystemCveMatch("macos", "14.5", "CVE-2025-9999") });
        vulnerabilityRepository.Setup(r => r.GetPackageCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PackageCveMatch>());
        var installedPackageRepository = new Mock<IInstalledPackageRepository>();

        var result = await new GetHostsQueryHandler(
                repository.Object, upgradePathRepository.Object, vulnerabilityRepository.Object, installedPackageRepository.Object)
            .Handle(new GetHostsQuery(), CancellationToken.None);

        Assert.Equal(1, result.Single(h => h.Id == hostBehind.Id).UnpatchedCveCount);
        Assert.Equal(0, result.Single(h => h.Id == hostBehind.Id).PatchedCveCount);
        Assert.Equal(0, result.Single(h => h.Id == hostCurrent.Id).UnpatchedCveCount);
        Assert.Equal(1, result.Single(h => h.Id == hostCurrent.Id).PatchedCveCount);
        Assert.Equal(1, result.Single(h => h.Id == hostUnknown.Id).UnpatchedCveCount);
        Assert.Equal(0, result.Single(h => h.Id == hostUnknown.Id).PatchedCveCount);
    }

    [Fact]
    public async Task GetHostsQueryHandler_CountsDistributionPackageCves_AsUnpatched()
    {
        var host = new Host("host-1", "SERIAL-1", "Ubuntu 22.04");
        var repository = new Mock<IHostRepository>();
        repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { host });
        var upgradePathRepository = new Mock<IUpgradePathRepository>();
        upgradePathRepository.Setup(r => r.GetInstallationPatchStatusesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<InstallationPatchStatus>());
        var vulnerabilityRepository = new Mock<IVulnerabilityRepository>();
        vulnerabilityRepository.Setup(r => r.GetApplicationCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ApplicationCveMatch>());
        vulnerabilityRepository.Setup(r => r.GetOperatingSystemCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<OperatingSystemCveMatch>());
        vulnerabilityRepository.Setup(r => r.GetPackageCveMatchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PackageCveMatch("Ubuntu:22.04", "openssl", "3.0.2-0ubuntu1.15", "CVE-2023-4911") });
        var installedPackageRepository = new Mock<IInstalledPackageRepository>();
        installedPackageRepository.Setup(r => r.GetHostPackageTriplesAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new HostPackageTriple(host.Id, new PackageTriple("Ubuntu:22.04", "openssl", "3.0.2-0ubuntu1.15")) });

        var result = await new GetHostsQueryHandler(
                repository.Object, upgradePathRepository.Object, vulnerabilityRepository.Object, installedPackageRepository.Object)
            .Handle(new GetHostsQuery(), CancellationToken.None);

        var dto = Assert.Single(result);
        Assert.Equal(1, dto.UnpatchedCveCount);
        Assert.Equal(0, dto.PatchedCveCount);
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
            new UpgradePathSummaryDto("Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script, null, null, null, null, null, DateTimeOffset.UtcNow, 3, 1, 2, Array.Empty<string>(), Array.Empty<string>()),
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
