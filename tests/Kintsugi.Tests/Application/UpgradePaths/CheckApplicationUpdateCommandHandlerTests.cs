using Moq;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.CheckApplicationUpdate;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class CheckApplicationUpdateCommandHandlerTests
{
    private readonly Mock<IUpgradePathRepository> _repository = new();
    private readonly Mock<IUpgradePathResearchClient> _researchClient = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IManagedApplicationRepository> _managed = new();

    private CheckApplicationUpdateCommandHandler CreateHandler() =>
        new(_repository.Object, _researchClient.Object, _unitOfWork.Object, _managed.Object);

    [Fact]
    public async Task Handle_ForAnApplicationAnotherToolPatches_IsSkippedWithTheOwner_AndRunsNothing()
    {
        _managed.Setup(m => m.FindAsync("Firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ManagedApplication.Create("Firefox", "NinjaOne"));
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(ScriptPath());

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.False(result.Success);
        Assert.Equal("Managed by NinjaOne; Kintsugi does not check it.", result.Note);
        _researchClient.Verify(
            c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static UpgradePath ScriptPath(string? latestVersion = "1.0", bool signed = true)
    {
        var path = UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, latestVersion, UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...", "org.mozilla.firefox");
        if (signed)
        {
            path.SignScript("reviewed-signature");
        }

        return path;
    }

    [Fact]
    public async Task Handle_UnsignedAiScript_IsSkipped_AndNeverRun()
    {
        // htw-secops/kintsugi#8: "Check for Updates" used to run every row's script, reviewed or not.
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(ScriptPath(signed: false));

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.False(result.Success);
        _researchClient.Verify(
            c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_UnsignedPackageManagerScript_ThatThisServerWrites_IsStillChecked()
    {
        var homebrew = PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew);
        var path = UpgradePath.Create(
            "wget", homebrew, UpgradePathStatus.Found, "1.0", UpgradeMethod.Script,
            null, null, null, null, null, HomebrewUpgradeScript.Build(), "wget");
        _repository.Setup(r => r.GetAsync("wget", homebrew, It.IsAny<CancellationToken>())).ReturnsAsync(path);
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync(HomebrewUpgradeScript.Build(), null, homebrew, "wget", "wget", It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.25");

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("wget", homebrew), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("1.25", path.LatestVersion);
    }

    [Fact]
    public async Task Handle_ScriptPathWithANewerVersion_UpdatesLatestVersion_AndReportsChanged()
    {
        var existing = ScriptPath("128.0");
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync("#!/bin/bash\n...", It.IsAny<string?>(), PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync("129.0");

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.VersionChanged);
        Assert.Equal("129.0", existing.LatestVersion);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ScriptPathWithTheSameVersion_SavesTheCheckedTimestamp_ButReportsUnchanged()
    {
        var existing = ScriptPath("129.0");
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync("#!/bin/bash\n...", It.IsAny<string?>(), PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync("129.0");

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.VersionChanged);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NeverCallsTheAi_RegardlessOfOutcome()
    {
        var existing = ScriptPath("128.0");
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        _researchClient.Verify(c => c.GenerateScriptAsync(It.IsAny<AiProviderSettings>(), It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheScriptDoesNotReportAVersion_ReportsFailure_WithoutTouchingTheExistingVersion()
    {
        var existing = ScriptPath("128.0");
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.False(result.Success);
        // A script that ran and answered nothing has failed at something, unlike the guards above.
        Assert.False(result.Skipped);
        Assert.False(result.VersionChanged);
        Assert.Equal("128.0", existing.LatestVersion);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenNoUpgradePathExists_ReportsFailure_WithoutCallingTheResearchClient()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Skipped);
        _researchClient.Verify(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheExistingPathIsNotAScript_ReportsFailure_WithoutCallingTheResearchClient()
    {
        var existing = UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, "1.0", UpgradeMethod.PackageManagerCommand,
            null, "brew upgrade firefox", null, null, null);
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Skipped);
        _researchClient.Verify(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Every guard above reports Skipped rather than a plain failure, and this is the one the
    // fleet-wide run trips over most: GetScriptUpgradePathsAsync selects on Method and Script
    // alone, so a row with no identifier is enumerated as a target and then declined here.
    // Counted as a failure it produced runs reporting failures against rows the Applications table
    // shows as perfectly healthy — the "Check Failed" badge is UpgradePathStatus.Failed, which
    // only the AI scan writes.
    [Fact]
    public async Task Handle_WhenTheScriptPathHasNoApplicationIdentifier_ReportsSkipped_WithoutCallingTheResearchClient()
    {
        var existing = UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, "1.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...", null);
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Skipped);
        Assert.Equal("No update script to check.", result.Note);
        _researchClient.Verify(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheResearchClientThrows_ReportsFailure_RatherThanPropagatingTheException()
    {
        var existing = ScriptPath("128.0");
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("subprocess timed out"));

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Skipped);
        Assert.Contains("subprocess timed out", result.Note);
    }
}
