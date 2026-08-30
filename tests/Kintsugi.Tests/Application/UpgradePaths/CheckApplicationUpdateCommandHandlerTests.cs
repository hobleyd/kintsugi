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

    private CheckApplicationUpdateCommandHandler CreateHandler() =>
        new(_repository.Object, _researchClient.Object, _unitOfWork.Object);

    private static UpgradePath ScriptPath(string? latestVersion = "1.0") =>
        UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, latestVersion, UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...", "org.mozilla.firefox");

    [Fact]
    public async Task Handle_ScriptPathWithANewerVersion_UpdatesLatestVersion_AndReportsChanged()
    {
        var existing = ScriptPath("128.0");
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync("#!/bin/bash\n...", "Firefox", "org.mozilla.firefox", It.IsAny<CancellationToken>()))
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
            .Setup(c => c.CheckScriptVersionAsync("#!/bin/bash\n...", "Firefox", "org.mozilla.firefox", It.IsAny<CancellationToken>()))
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
            .Setup(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.False(result.Success);
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
        _researchClient.Verify(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _researchClient.Verify(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheResearchClientThrows_ReportsFailure_RatherThanPropagatingTheException()
    {
        var existing = ScriptPath("128.0");
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("subprocess timed out"));

        var result = await CreateHandler().Handle(new CheckApplicationUpdateCommand("Firefox", PlatformBucket.MacOs), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("subprocess timed out", result.Note);
    }
}
