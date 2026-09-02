using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths.Commands.SaveUpgradePath;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class SaveUpgradePathCommandHandlerTests
{
    private readonly Mock<IUpgradePathRepository> _repository = new();
    private readonly Mock<IArtifactSigningService> _artifactSigningService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public SaveUpgradePathCommandHandlerTests()
    {
        _artifactSigningService.Setup(s => s.Sign(It.IsAny<string>())).Returns<string?>(content => content is null ? null : $"signed:{content}");
    }

    private SaveUpgradePathCommandHandler CreateHandler() => new(_repository.Object, _artifactSigningService.Object, _unitOfWork.Object);

    private static SaveUpgradePathCommand Command(UpgradeMethod method = UpgradeMethod.Script, string? script = "the-script", string? command = null) =>
        new("Firefox", "macOS", "128.0", method, null, command, null, null, null, script);

    [Fact]
    public async Task Handle_WhenNoRowExistsYet_CreatesOne()
    {
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        var result = await CreateHandler().Handle(Command(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        _repository.Verify(r => r.AddAsync(It.IsAny<UpgradePath>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithMethodUnknown_DerivesStatusNotFound()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        var result = await CreateHandler().Handle(Command(method: UpgradeMethod.Unknown, script: null), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Handle_WhenARowAlreadyExists_UpdatesItInPlaceRatherThanCreatingASecondOne()
    {
        var existing = UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "127.0", UpgradeMethod.Script, null, null, null, null, null, "old-script");
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(Command(script: "new-script"), CancellationToken.None);

        Assert.Equal("new-script", result.Script);
        Assert.Equal("128.0", existing.LatestVersion);
        _repository.Verify(r => r.AddAsync(It.IsAny<UpgradePath>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_SignsWhateverEndedUpOnTheSavedEntity_AndPersistsBothSignatures()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        await CreateHandler().Handle(Command(method: UpgradeMethod.PackageManagerCommand, script: null, command: "brew upgrade firefox"), CancellationToken.None);

        _artifactSigningService.Verify(s => s.Sign("brew upgrade firefox"), Times.Once);
        _repository.Verify(r => r.AddAsync(
            It.Is<UpgradePath>(p => p.CommandSignature == "signed:brew upgrade firefox" && p.ScriptSignature == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NeverSignsAScript_RequiringManualReviewBeforeSigning()
    {
        // Whether AI-generated or hand-pasted, a script must never come back pre-signed — only the
        // "Sign Script" action, after a human has reviewed it, sets ScriptSignature.
        _repository.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        await CreateHandler().Handle(Command(script: "new-script"), CancellationToken.None);

        _artifactSigningService.Verify(s => s.Sign("new-script"), Times.Never);
        _repository.Verify(r => r.AddAsync(It.Is<UpgradePath>(p => p.ScriptSignature == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ResavingAnAlreadySignedScriptWithNewContent_ClearsTheStaleSignature()
    {
        // A previously-signed script's signature vouched for its old content — new content must
        // never keep carrying that stale signature forward.
        var existing = UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "127.0", UpgradeMethod.Script, null, null, null, null, null, "old-script");
        existing.SignScript("signed:old-script");
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(Command(script: "new-script"), CancellationToken.None);

        Assert.Null(existing.ScriptSignature);
        Assert.False(result.ScriptSigned);
    }

    [Fact]
    public async Task Handle_AlwaysSavesBeforeReturning()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        await CreateHandler().Handle(Command(), CancellationToken.None);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithNoApplicationIdentifierGiven_DefaultsItToTheApplicationName()
    {
        // CheckApplicationUpdateCommandHandler refuses to run --update-version at all when
        // ApplicationIdentifier is blank, so a hand-saved row with none would never pick up a
        // LatestVersion from "Find updates" again — this default is what the AI research flow
        // already relies on for the same reason.
        _repository.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        UpgradePath? saved = null;
        _repository.Setup(r => r.AddAsync(It.IsAny<UpgradePath>(), It.IsAny<CancellationToken>()))
            .Callback<UpgradePath, CancellationToken>((p, _) => saved = p)
            .Returns(Task.CompletedTask);

        await CreateHandler().Handle(Command(), CancellationToken.None);

        Assert.Equal("Firefox", saved?.ApplicationIdentifier);
    }

    [Fact]
    public async Task Handle_WithAnExplicitApplicationIdentifier_UsesItRatherThanTheApplicationName()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        UpgradePath? saved = null;
        _repository.Setup(r => r.AddAsync(It.IsAny<UpgradePath>(), It.IsAny<CancellationToken>()))
            .Callback<UpgradePath, CancellationToken>((p, _) => saved = p)
            .Returns(Task.CompletedTask);

        var command = Command() with { ApplicationIdentifier = "org.mozilla.firefox" };
        await CreateHandler().Handle(command, CancellationToken.None);

        Assert.Equal("org.mozilla.firefox", saved?.ApplicationIdentifier);
    }
}
