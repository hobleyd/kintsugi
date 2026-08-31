using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.SignUpgradePathScript;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class SignUpgradePathScriptCommandHandlerTests
{
    private readonly Mock<IUpgradePathRepository> _repository = new();
    private readonly Mock<IArtifactSigningService> _artifactSigningService = new();
    private readonly Mock<IUpgradePathResearchClient> _researchClient = new();
    private readonly Mock<IScriptApprovalPublisher> _approvalPublisher = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public SignUpgradePathScriptCommandHandlerTests()
    {
        _artifactSigningService.Setup(s => s.Sign(It.IsAny<string>())).Returns<string?>(content => content is null ? null : $"signed:{content}");
        // No other unsigned row shares this script's content by default — individual tests override
        // this to exercise propagating a freshly-applied signature to sibling rows.
        _repository.Setup(r => r.GetUnsignedRowsWithScriptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<UpgradePath>());
        _artifactSigningService.Setup(s => s.GetPublicKeyPem()).Returns("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n");
        _artifactSigningService.Setup(s => s.GetPublicKeyFingerprint()).Returns("sha256:0123456789abcdef");
        // Publication is best-effort by design, so the default here is the boring success case;
        // Handle_WhenPublishingTheApprovalFails_StillSaves covers the other one.
        _approvalPublisher
            .Setup(p => p.PublishAsync(It.IsAny<ScriptApprovalSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptApprovalPublishResult(ScriptApprovalPublishOutcome.PullRequestOpened, "https://example.invalid/pull/1"));
    }

    private SignUpgradePathScriptCommandHandler CreateHandler() => new(_repository.Object, _artifactSigningService.Object, _researchClient.Object, _approvalPublisher.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_SignsTheAlreadyPersistedScript_AndSaves()
    {
        var existing = UpgradePath.Create(
            "Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...");
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(new SignUpgradePathScriptCommand("Firefox", "macOS"), CancellationToken.None);

        Assert.Equal("signed:#!/bin/bash\n...", existing.ScriptSignature);
        Assert.Equal("#!/bin/bash\n...", result.Script);
        Assert.True(result.ScriptSigned);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenApplicationIdentifierIsKnown_RunsUpdateVersionAndRecordsTheDiscoveredLatestVersion()
    {
        var existing = UpgradePath.Create(
            "Firefox", "macOS", UpgradePathStatus.Found, null, UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...", "org.mozilla.firefox");
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync("#!/bin/bash\n...", PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync("129.0");

        var result = await CreateHandler().Handle(new SignUpgradePathScriptCommand("Firefox", "macOS"), CancellationToken.None);

        Assert.Equal("129.0", existing.LatestVersion);
        Assert.Equal("129.0", result.LatestVersion);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenApplicationIdentifierIsUnknown_SkipsTheUpdateVersionCheck()
    {
        var existing = UpgradePath.Create(
            "Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...");
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(new SignUpgradePathScriptCommand("Firefox", "macOS"), CancellationToken.None);

        Assert.Equal("128.0", result.LatestVersion);
        _researchClient.Verify(
            c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenNoUpgradePathExists_ThrowsNotFound()
    {
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateHandler().Handle(new SignUpgradePathScriptCommand("Firefox", "macOS"), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_WhenTheUpgradePathHasNoScript_ThrowsDomainException_WithoutSigningAnything()
    {
        var existing = UpgradePath.Create(
            "Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.PackageManagerCommand,
            null, "brew upgrade firefox", null, null, null);
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        await Assert.ThrowsAsync<DomainException>(
            () => CreateHandler().Handle(new SignUpgradePathScriptCommand("Firefox", "macOS"), CancellationToken.None));

        _artifactSigningService.Verify(s => s.Sign(It.IsAny<string>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_PropagatesTheFreshSignature_ToEveryOtherUnsignedRowSharingTheIdenticalScript()
    {
        // Every Homebrew script is now byte-identical across every application (see
        // HomebrewUpgradeScript.Build) — signing one is a human vouching for that exact content,
        // so every other already-resolved row sharing it should become trusted immediately too.
        var firefox = UpgradePath.Create(
            "Firefox", PlatformBucket.Generic, UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\nshared script\n");
        var wget = UpgradePath.Create(
            "wget", PlatformBucket.Generic, UpgradePathStatus.Found, "1.21", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\nshared script\n");
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.Generic, It.IsAny<CancellationToken>())).ReturnsAsync(firefox);
        _repository
            .Setup(r => r.GetUnsignedRowsWithScriptAsync("#!/bin/bash\nshared script\n", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpgradePath> { firefox, wget });

        await CreateHandler().Handle(new SignUpgradePathScriptCommand("Firefox", PlatformBucket.Generic), CancellationToken.None);

        Assert.Equal("signed:#!/bin/bash\nshared script\n", firefox.ScriptSignature);
        Assert.Equal("signed:#!/bin/bash\nshared script\n", wget.ScriptSignature);
    }

    [Fact]
    public async Task Handle_LeavesTheCommandSignatureUntouched()
    {
        var existing = UpgradePath.Create(
            "Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, "brew upgrade firefox", null, null, null, "#!/bin/bash\n...");
        existing.SetSignatures(null, "signed:brew upgrade firefox");
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        await CreateHandler().Handle(new SignUpgradePathScriptCommand("Firefox", "macOS"), CancellationToken.None);

        Assert.Equal("signed:brew upgrade firefox", existing.CommandSignature);
    }

    [Fact]
    public async Task Handle_PublishesTheApproval_OverExactlyTheBytesItSigned()
    {
        var existing = UpgradePath.Create(
            "Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...", "org.mozilla.firefox");
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(
            new SignUpgradePathScriptCommand("Firefox", "macOS", SignedBy: "someone@example.invalid"), CancellationToken.None);

        _approvalPublisher.Verify(p => p.PublishAsync(
            It.Is<ScriptApprovalSubmission>(s =>
                s.Script == "#!/bin/bash\n..."
                // The hash the corpus is keyed by has to describe the same bytes the signature does,
                // or an importing server rejects the entry as not matching its own directory name.
                && s.Sha256 == ScriptContentHash.Of("#!/bin/bash\n...")
                && s.Signature == "signed:#!/bin/bash\n..."
                && s.PlatformBucket == "macOS"
                && s.Language == ScriptLanguage.Bash
                && s.ApplicationIdentifier == "org.mozilla.firefox"
                && s.SignedBy == "someone@example.invalid"),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(ScriptApprovalPublishOutcome.PullRequestOpened, result.ApprovalOutcome);
        Assert.Equal("https://example.invalid/pull/1", result.ApprovalPullRequestUrl);
    }

    [Fact]
    public async Task Handle_PublishesOnlyAfterTheLocalApprovalIsSaved()
    {
        var existing = UpgradePath.Create(
            "Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...");
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var order = new List<string>();
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).Callback(() => order.Add("save"));
        _approvalPublisher
            .Setup(p => p.PublishAsync(It.IsAny<ScriptApprovalSubmission>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("publish"))
            .ReturnsAsync(new ScriptApprovalPublishResult(ScriptApprovalPublishOutcome.PullRequestOpened));

        await CreateHandler().Handle(new SignUpgradePathScriptCommand("Firefox", "macOS"), CancellationToken.None);

        // Order matters: a pull request proposing an approval that a failed save then discarded would
        // leave a merged record of a decision nobody made.
        Assert.Equal(new[] { "save", "publish" }, order);
    }

    [Fact]
    public async Task Handle_WhenPublishingReportsFailure_TheLocalSignatureStillStands()
    {
        var existing = UpgradePath.Create(
            "Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...");
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _approvalPublisher
            .Setup(p => p.PublishAsync(It.IsAny<ScriptApprovalSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptApprovalPublishResult(ScriptApprovalPublishOutcome.Failed, Message: "GitHub said no."));

        var result = await CreateHandler().Handle(new SignUpgradePathScriptCommand("Firefox", "macOS"), CancellationToken.None);

        // The whole reason publication is best-effort: a GitHub outage must not stop a reviewed
        // script from patching the fleet it was reviewed for.
        Assert.Equal("signed:#!/bin/bash\n...", existing.ScriptSignature);
        Assert.True(result.ScriptSigned);
        Assert.Equal(ScriptApprovalPublishOutcome.Failed, result.ApprovalOutcome);
        Assert.Equal("GitHub said no.", result.ApprovalMessage);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
