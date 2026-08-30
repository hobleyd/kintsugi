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
        // Always the fixed "generic" bucket, never the real per-host OS platform — the row this
        // matches (whether seeded at registration time or by "Find Upgrade Paths") is stored under
        // "generic" too, regardless of which OS actually reported it (see UpgradePath.Platform).
        Assert.Equal(PlatformBucket.Generic, item.Platform);
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
        Assert.Equal(PlatformBucket.Generic, homebrewItem.Platform);
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
        Assert.Contains(plan.WorkItems, i => i.Platform == "Windows");
    }

    private void SetUpEnabledAiSettings() =>
        _aiAgentSettingsRepository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AiAgentSettings.Create(AiProvider.Anthropic, "sk-123", null, null, isEnabled: true));
}
