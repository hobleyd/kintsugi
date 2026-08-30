using Moq;
using Kintsugi.Application.Applications.Commands.RegisterApplications;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application;

public class RegisterApplicationsCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hostRepository = new();
    private readonly Mock<IInstalledApplicationRepository> _installedApplicationRepository = new();
    private readonly Mock<IUpgradePathRepository> _upgradePathRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Host _host = new("host-1", "SERIAL-1", "macOS 15.0");

    /// <summary>Where every Homebrew-managed row lives — its manager's own bucket, not the host's
    /// OS bucket and not the old shared "generic" one. See PlatformBucket.ForPackageManager.</summary>
    private static readonly string HomebrewBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew);

    private RegisterApplicationsCommandHandler CreateHandler() =>
        new(_hostRepository.Object, _installedApplicationRepository.Object, _upgradePathRepository.Object, _unitOfWork.Object);

    private void SetUpHost(Host? host)
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(host);
        _installedApplicationRepository
            .Setup(r => r.GetByHostIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstalledApplication>());
        // No pre-existing rows for any application by default — individual tests override this to
        // exercise the legacy-row cleanup in UpsertPackageManagerUpgradePathsAsync.
        _upgradePathRepository
            .Setup(r => r.GetAllForApplicationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpgradePath>());
        // No already-signed row anywhere with matching script content by default — individual tests
        // override this to exercise the signature-inheritance behavior.
        _upgradePathRepository
            .Setup(r => r.FindExistingSignatureForScriptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    [Fact]
    public async Task Handle_WhenNoHostWithThatSerialNumberIsRegistered_ThrowsNotFound()
    {
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("MISSING", It.IsAny<CancellationToken>())).ReturnsAsync((Host?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(
            new RegisterApplicationsCommand("MISSING", Array.Empty<ApplicationEntry>()), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_RemovesEveryPreviouslyReportedApplicationForThisHost_BeforeAddingTheNewReport()
    {
        var previouslyReported = new List<InstalledApplication> { new(_host.Id, "OldApp", "1.0.0") };
        _hostRepository.Setup(r => r.GetBySerialNumberAsync("SERIAL-1", It.IsAny<CancellationToken>())).ReturnsAsync(_host);
        _installedApplicationRepository.Setup(r => r.GetByHostIdAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(previouslyReported);

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[] { new ApplicationEntry("NewApp", "2.0.0") }), CancellationToken.None);

        _installedApplicationRepository.Verify(r => r.RemoveRange(previouslyReported), Times.Once);
    }

    [Fact]
    public async Task Handle_ReportsBackTheCountOfNewlyAddedApplications_DedupingRepeatedNames()
    {
        SetUpHost(_host);

        var result = await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[]
            {
                new ApplicationEntry("Firefox", "128.0"),
                new ApplicationEntry("Firefox", "128.0"), // duplicate name — first one wins
                new ApplicationEntry("Slack", "4.0.0"),
            }),
            CancellationToken.None);

        Assert.Equal(_host.Id, result.HostId);
        Assert.Equal(2, result.ApplicationCount);
    }

    [Fact]
    public async Task Handle_LinksAChildEntryToItsPackageManagersOwnEntity_WhenBothAreInTheSameReport()
    {
        SetUpHost(_host);
        List<InstalledApplication>? added = null;
        _installedApplicationRepository
            .Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<InstalledApplication>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<InstalledApplication>, CancellationToken>((apps, _) => added = apps.ToList());

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[]
            {
                new ApplicationEntry("Homebrew", "4.3.9"),
                new ApplicationEntry("firefox", "128.0", PackageManager: "Homebrew"),
            }),
            CancellationToken.None);

        var homebrew = added!.Single(a => a.Name == "Homebrew");
        var firefox = added!.Single(a => a.Name == "firefox");
        Assert.Equal(homebrew.Id, firefox.ParentApplicationId);
    }

    [Fact]
    public async Task Handle_LeavesAnEntryStandalone_WhenItsClaimedPackageManagerIsNotInTheSameReport()
    {
        SetUpHost(_host);
        List<InstalledApplication>? added = null;
        _installedApplicationRepository
            .Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<InstalledApplication>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<InstalledApplication>, CancellationToken>((apps, _) => added = apps.ToList());

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[] { new ApplicationEntry("firefox", "128.0", PackageManager: "SomeUnreportedManager") }),
            CancellationToken.None);

        Assert.Null(added!.Single().ParentApplicationId);
    }

    [Fact]
    public async Task Handle_SeedsANewUpgradePath_FromAPackageManagerReportedAvailableVersion()
    {
        SetUpHost(_host);
        _upgradePathRepository.Setup(r => r.GetAsync("firefox", HomebrewBucket, It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[]
            {
                new ApplicationEntry("firefox", "128.0", PackageManager: "Homebrew", AvailableVersion: "129.0"),
            }),
            CancellationToken.None);

        // Stored under Homebrew's own bucket (not the host's real OS) and as a Method.Script row
        // (not PackageManagerCommand) — the same shape "Find Upgrade Paths" produces — so this row
        // is findable by the (application, manager-bucket) key and an agent recognizes it as
        // patchable once signed. The script text itself never names "firefox" —
        // it reads --appName at runtime instead (see HomebrewUpgradeScript.Build) — so every other
        // Homebrew-managed application gets this exact same content.
        _upgradePathRepository.Verify(r => r.AddAsync(
            It.Is<UpgradePath>(p => p.ApplicationName == "firefox" && p.Platform == HomebrewBucket && p.LatestVersion == "129.0"
                && p.Method == UpgradeMethod.Script && p.Command == null && p.Script != null
                && !p.Script.Contains("firefox") && p.Script.Contains("brew update && brew upgrade \"$APP_NAME\"")
                && p.ApplicationIdentifier == "firefox"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_InheritsAnExistingSignature_WhenCreatingARow_AndIdenticalScriptContentIsAlreadySigned()
    {
        SetUpHost(_host);
        _upgradePathRepository.Setup(r => r.GetAsync("firefox", HomebrewBucket, It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);
        _upgradePathRepository
            .Setup(r => r.FindExistingSignatureForScriptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("signed:already-reviewed-elsewhere");

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[]
            {
                new ApplicationEntry("firefox", "128.0", PackageManager: "Homebrew", AvailableVersion: "129.0"),
            }),
            CancellationToken.None);

        _upgradePathRepository.Verify(r => r.AddAsync(
            It.Is<UpgradePath>(p => p.ScriptSignature == "signed:already-reviewed-elsewhere"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_InheritsAnExistingSignature_WhenUpdatingAnUnsignedRow_AndIdenticalScriptContentIsAlreadySigned()
    {
        SetUpHost(_host);
        var existingPath = UpgradePath.Create(
            "firefox", HomebrewBucket, UpgradePathStatus.Found, "127.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...", "firefox");
        _upgradePathRepository.Setup(r => r.GetAsync("firefox", HomebrewBucket, It.IsAny<CancellationToken>())).ReturnsAsync(existingPath);
        _upgradePathRepository
            .Setup(r => r.FindExistingSignatureForScriptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("signed:already-reviewed-elsewhere");

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[]
            {
                new ApplicationEntry("firefox", "128.0", PackageManager: "Homebrew", AvailableVersion: "129.0"),
            }),
            CancellationToken.None);

        Assert.Equal("signed:already-reviewed-elsewhere", existingPath.ScriptSignature);
    }

    [Fact]
    public async Task Handle_UpdatesAnExistingUpgradePath_FromAPackageManagerReportedAvailableVersion()
    {
        SetUpHost(_host);
        var existingPath = UpgradePath.Create(
            "firefox", HomebrewBucket, UpgradePathStatus.Found, "127.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...", "firefox");
        _upgradePathRepository.Setup(r => r.GetAsync("firefox", HomebrewBucket, It.IsAny<CancellationToken>())).ReturnsAsync(existingPath);

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[]
            {
                new ApplicationEntry("firefox", "128.0", PackageManager: "Homebrew", AvailableVersion: "129.0"),
            }),
            CancellationToken.None);

        Assert.Equal("129.0", existingPath.LatestVersion);
        _upgradePathRepository.Verify(r => r.AddAsync(It.IsAny<UpgradePath>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_LeavesAnExistingUpgradePathsScriptSignatureUntouched_OnRepeatRegistration()
    {
        // The deterministic script content never changes for a given package name, so an admin's
        // prior "Sign Script" review must survive every subsequent routine inventory report —
        // otherwise a signed, patchable row would flip back to unsigned on the agent's very next
        // check-in.
        SetUpHost(_host);
        var existingPath = UpgradePath.Create(
            "firefox", HomebrewBucket, UpgradePathStatus.Found, "127.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...", "firefox");
        existingPath.SignScript("signed:already-approved");
        _upgradePathRepository.Setup(r => r.GetAsync("firefox", HomebrewBucket, It.IsAny<CancellationToken>())).ReturnsAsync(existingPath);

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[]
            {
                new ApplicationEntry("firefox", "128.0", PackageManager: "Homebrew", AvailableVersion: "129.0"),
            }),
            CancellationToken.None);

        Assert.Equal("signed:already-approved", existingPath.ScriptSignature);
    }

    [Fact]
    public async Task Handle_RetiresALegacyPackageManagerCommandRow_StoredUnderTheRealOsPlatform()
    {
        // Reproduces a row this handler used to write before Homebrew moved to a fixed
        // per-manager/Script shape: stored under the host's real OS platform, as
        // PackageManagerCommand. Left in place, it would keep winning GetSummariesAsync's per-host
        // platform lookup (tried before its package-manager fallback) and permanently shadow the
        // correctly-shaped row below.
        SetUpHost(_host);
        var legacyRow = UpgradePath.Create(
            "firefox", "macOS", UpgradePathStatus.Found, "127.0", UpgradeMethod.PackageManagerCommand,
            null, "brew upgrade firefox", null, null, null);
        _upgradePathRepository
            .Setup(r => r.GetAllForApplicationAsync("firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpgradePath> { legacyRow });
        _upgradePathRepository.Setup(r => r.GetAsync("firefox", HomebrewBucket, It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[]
            {
                new ApplicationEntry("firefox", "128.0", PackageManager: "Homebrew", AvailableVersion: "129.0"),
            }),
            CancellationToken.None);

        _upgradePathRepository.Verify(r => r.Remove(legacyRow), Times.Once);
        _upgradePathRepository.Verify(r => r.AddAsync(
            It.Is<UpgradePath>(p => p.Platform == HomebrewBucket && p.Method == UpgradeMethod.Script),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_SeedsAWingetRow_UnderWingetsOwnBucket_WithAPowerShellScript()
    {
        SetUpHost(_host);
        var wingetBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget);
        _upgradePathRepository.Setup(r => r.GetAsync("VLC media player", wingetBucket, It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[]
            {
                new ApplicationEntry("VLC media player", "3.0.20", PackageManager: PackageManagerCatalog.Winget,
                    ApplicationIdentifier: "VideoLAN.VLC", AvailableVersion: "3.0.21"),
            }),
            CancellationToken.None);

        // A separate bucket from Homebrew's, carrying a PowerShell script and the winget package id
        // (not the display name) as its identifier — winget addresses a package by id.
        _upgradePathRepository.Verify(r => r.AddAsync(
            It.Is<UpgradePath>(p => p.Platform == wingetBucket && p.Method == UpgradeMethod.Script
                && p.ApplicationIdentifier == "VideoLAN.VLC" && p.Script != null
                && !p.Script.Contains("#!/bin/bash") && p.Script.Contains("winget upgrade")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_DoesNotSeedAnUpgradePath_ForAnUnrecognizedPackageManager()
    {
        // There is no script to write for a manager this system doesn't know how to drive —
        // seeding one anyway would previously have written Homebrew's bash script for it.
        SetUpHost(_host);

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[]
            {
                new ApplicationEntry("some-package", "1.0", PackageManager: "SomeNewManager", AvailableVersion: "1.1"),
            }),
            CancellationToken.None);

        _upgradePathRepository.Verify(r => r.AddAsync(It.IsAny<UpgradePath>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DoesNotTouchUpgradePaths_ForEntriesWithNoAvailableVersionReported()
    {
        SetUpHost(_host);

        await CreateHandler().Handle(
            new RegisterApplicationsCommand("SERIAL-1", new[] { new ApplicationEntry("firefox", "128.0", PackageManager: "Homebrew") }),
            CancellationToken.None);

        _upgradePathRepository.Verify(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
