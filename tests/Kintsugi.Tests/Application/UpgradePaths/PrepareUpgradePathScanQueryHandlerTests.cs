using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Queries.PrepareUpgradePathScan;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class PrepareUpgradePathScanQueryHandlerTests
{
    private readonly Mock<IAiAgentSettingsRepository> _aiAgentSettingsRepository = new();
    private readonly Mock<IInstalledApplicationRepository> _installedApplicationRepository = new();

    private PrepareUpgradePathScanQueryHandler CreateHandler() => new(_aiAgentSettingsRepository.Object, _installedApplicationRepository.Object);

    [Fact]
    public async Task Handle_WhenNoAiSettingsAreSaved_ReturnsAnUnconfiguredPlan_ButStillBuildsWorkItems()
    {
        _aiAgentSettingsRepository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AiAgentSettings?)null);
        _installedApplicationRepository.Setup(r => r.GetApplicationVersionVariantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationVersionVariantDto("Firefox", null, "macOS 15.0", "128.0"),
        });

        var plan = await CreateHandler().Handle(new PrepareUpgradePathScanQuery(), CancellationToken.None);

        Assert.False(plan.AiConfigured);
        Assert.Null(plan.Settings);
        // Still built — a package-manager-managed application in the same plan would need to
        // resolve without AI, and a single-row refresh needs to find its matching item regardless
        // of AI configuration. ResearchApplicationUpgradePathCommandHandler is what actually skips
        // the AI call for a Research item when Settings is null.
        Assert.Single(plan.WorkItems);
    }

    [Fact]
    public async Task Handle_WhenAiSettingsExistButAreDisabled_ReturnsAnUnconfiguredPlan_ButStillBuildsWorkItems()
    {
        _aiAgentSettingsRepository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AiAgentSettings.Create(AiProvider.Anthropic, "sk-123", null, null, isEnabled: false));
        _installedApplicationRepository.Setup(r => r.GetApplicationVersionVariantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationVersionVariantDto("Firefox", null, "macOS 15.0", "128.0"),
        });

        var plan = await CreateHandler().Handle(new PrepareUpgradePathScanQuery(), CancellationToken.None);

        Assert.False(plan.AiConfigured);
        Assert.Null(plan.Settings);
        Assert.Single(plan.WorkItems);
    }

    [Fact]
    public async Task Handle_WhenAiIsNotConfigured_APackageManagerManagedApplicationIsStillIncluded()
    {
        _aiAgentSettingsRepository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AiAgentSettings?)null);
        _installedApplicationRepository.Setup(r => r.GetApplicationVersionVariantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationVersionVariantDto("firefox", "Homebrew", "macOS 15.0", "128.0"),
        });

        var plan = await CreateHandler().Handle(new PrepareUpgradePathScanQuery(), CancellationToken.None);

        Assert.False(plan.AiConfigured);
        var item = Assert.Single(plan.WorkItems);
        Assert.Equal(UpgradePathWorkKind.PackageManagerManaged, item.Kind);
        Assert.Equal("Homebrew", item.PackageManagerName);
    }

    [Fact]
    public async Task Handle_AnApplicationManagedByAPackageManager_BecomesAPackageManagerManagedWorkItem()
    {
        SetUpEnabledAiSettings();
        _installedApplicationRepository.Setup(r => r.GetApplicationVersionVariantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationVersionVariantDto("firefox", "Homebrew", "macOS 15.0", "128.0"),
        });

        var plan = await CreateHandler().Handle(new PrepareUpgradePathScanQuery(), CancellationToken.None);

        var item = Assert.Single(plan.WorkItems);
        Assert.Equal(UpgradePathWorkKind.PackageManagerManaged, item.Kind);
        Assert.Equal("Homebrew", item.PackageManagerName);
        // The manager's own bucket, never the real per-host OS platform — the row this matches
        // (whether seeded at registration time or by "Find Upgrade Paths") is stored under that
        // same bucket regardless of which OS actually reported it (see UpgradePath.Platform).
        Assert.Equal(PlatformBucket.ForPackageManager("Homebrew"), item.Platform);
    }

    [Fact]
    public async Task Handle_ThePackageManagerItselfManaged_BecomesAPackageManagerSelfUpdateWorkItem()
    {
        SetUpEnabledAiSettings();
        _installedApplicationRepository.Setup(r => r.GetApplicationVersionVariantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationVersionVariantDto("Homebrew", null, "macOS 15.0", "4.3.9"),
            new ApplicationVersionVariantDto("firefox", "Homebrew", "macOS 15.0", "128.0"),
        });

        var plan = await CreateHandler().Handle(new PrepareUpgradePathScanQuery(), CancellationToken.None);

        var homebrewItem = plan.WorkItems.Single(i => i.ApplicationName == "Homebrew");
        Assert.Equal(UpgradePathWorkKind.PackageManagerSelfUpdate, homebrewItem.Kind);
        // A manager is its own manager, so its self-update row shares a bucket with everything it
        // manages — which is what lets the repository's fallback lookup find it with one rule.
        Assert.Equal(PlatformBucket.ForPackageManager("Homebrew"), homebrewItem.Platform);
    }

    [Fact]
    public async Task Handle_AnUnmanagedApplication_BecomesAResearchWorkItem_WithDistinctKnownVersions()
    {
        SetUpEnabledAiSettings();
        _installedApplicationRepository.Setup(r => r.GetApplicationVersionVariantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationVersionVariantDto("Firefox", null, "macOS 15.0", "128.0", "org.mozilla.firefox"),
            new ApplicationVersionVariantDto("Firefox", null, "macOS 14.0", "127.0", "org.mozilla.firefox"),
            new ApplicationVersionVariantDto("Firefox", null, "macOS 15.0", "128.0", "org.mozilla.firefox"), // duplicate variant
        });

        var plan = await CreateHandler().Handle(new PrepareUpgradePathScanQuery(), CancellationToken.None);

        var item = Assert.Single(plan.WorkItems);
        Assert.Equal(UpgradePathWorkKind.Research, item.Kind);
        Assert.Equal(PlatformBucket.MacOs, item.Platform);
        Assert.Equal(new[] { "128.0", "127.0" }, item.KnownVersions);
        Assert.Equal("org.mozilla.firefox", item.ApplicationIdentifier);
    }

    [Fact]
    public async Task Handle_TheSameApplicationOnDifferentPlatforms_BecomesOneResearchWorkItemPerPlatform()
    {
        SetUpEnabledAiSettings();
        _installedApplicationRepository.Setup(r => r.GetApplicationVersionVariantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationVersionVariantDto("Chrome", null, "macOS 15.0", "128.0"),
            new ApplicationVersionVariantDto("Chrome", null, "Windows 11", "128.0"),
        });

        var plan = await CreateHandler().Handle(new PrepareUpgradePathScanQuery(), CancellationToken.None);

        Assert.Equal(2, plan.WorkItems.Count);
        Assert.Contains(plan.WorkItems, i => i.Platform == PlatformBucket.MacOs);
        Assert.Contains(plan.WorkItems, i => i.Platform == PlatformBucket.Windows);
    }

    [Fact]
    public async Task Handle_TheSameApplicationManagedOnOnePlatformAndUnmanagedOnAnother_BuildsAWorkItemForEach()
    {
        SetUpEnabledAiSettings();
        _installedApplicationRepository.Setup(r => r.GetApplicationVersionVariantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationVersionVariantDto("VLC", "Homebrew", "macOS 15.0", "3.0.20"),
            new ApplicationVersionVariantDto("VLC", null, "Windows 11", "3.0.20", "VideoLAN.VLC"),
        });

        var plan = await CreateHandler().Handle(new PrepareUpgradePathScanQuery(), CancellationToken.None);

        // The whole point of splitting by variant rather than by application: treating the first
        // manager seen as covering the application everywhere meant the Windows install got no
        // work item at all, and so could never be researched.
        Assert.Equal(2, plan.WorkItems.Count);
        var managed = plan.WorkItems.Single(i => i.Kind == UpgradePathWorkKind.PackageManagerManaged);
        Assert.Equal(PlatformBucket.ForPackageManager("Homebrew"), managed.Platform);
        var research = plan.WorkItems.Single(i => i.Kind == UpgradePathWorkKind.Research);
        Assert.Equal(PlatformBucket.Windows, research.Platform);
    }

    [Fact]
    public async Task Handle_TheSameApplicationManagedByTwoDifferentPackageManagers_BuildsOneWorkItemPerManager()
    {
        SetUpEnabledAiSettings();
        _installedApplicationRepository.Setup(r => r.GetApplicationVersionVariantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationVersionVariantDto("vlc", "winget", "Windows 11", "3.0.20", "VideoLAN.VLC"),
            new ApplicationVersionVariantDto("vlc", "Chocolatey", "Windows 11", "3.0.20", "vlc"),
        });

        var plan = await CreateHandler().Handle(new PrepareUpgradePathScanQuery(), CancellationToken.None);

        // Two managers, two upgrade mechanisms, two rows — a single shared row would hand one
        // host's agent the other manager's script.
        Assert.Equal(2, plan.WorkItems.Count);
        Assert.Contains(plan.WorkItems, i => i.Platform == PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget));
        Assert.Contains(plan.WorkItems, i => i.Platform == PlatformBucket.ForPackageManager(PackageManagerCatalog.Chocolatey));
    }

    [Fact]
    public async Task Handle_APackageManagerManagedApplication_CarriesItsIdentifierThroughToTheWorkItem()
    {
        SetUpEnabledAiSettings();
        _installedApplicationRepository.Setup(r => r.GetApplicationVersionVariantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationVersionVariantDto("VLC media player", "winget", "Windows 11", "3.0.20", "VideoLAN.VLC"),
        });

        var plan = await CreateHandler().Handle(new PrepareUpgradePathScanQuery(), CancellationToken.None);

        // winget addresses a package by id, not display name — losing this would make every winget
        // row's --appId the (wrong) display name.
        Assert.Equal("VideoLAN.VLC", Assert.Single(plan.WorkItems).ApplicationIdentifier);
    }

    private void SetUpEnabledAiSettings() =>
        _aiAgentSettingsRepository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AiAgentSettings.Create(AiProvider.Anthropic, "sk-123", null, null, isEnabled: true));
}
