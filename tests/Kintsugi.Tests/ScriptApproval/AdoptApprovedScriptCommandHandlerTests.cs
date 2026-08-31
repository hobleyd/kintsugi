using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval.Commands.AdoptApprovedScript;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.ScriptApproval;

public class AdoptApprovedScriptCommandHandlerTests
{
    private const string BashScript = "#!/bin/bash\nbrew upgrade \"$2\"\n";
    private const string Pem = "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n";
    private const string Sha256 = "abc123";
    private const string Fingerprint = "sha256:1111";

    private readonly Mock<IApprovedScriptRepository> _approvedScripts = new();
    private readonly Mock<IUpgradePathRepository> _upgradePaths = new();
    private readonly Mock<IScriptSignatureVerifier> _verifier = new();
    private readonly Mock<IArtifactSigningService> _signingService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public AdoptApprovedScriptCommandHandlerTests()
    {
        _verifier.Setup(v => v.Verify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _signingService.Setup(s => s.Sign(It.IsAny<string>())).Returns<string?>(c => c is null ? null : $"localsig:{c}");
    }

    private AdoptApprovedScriptCommandHandler CreateHandler() => new(
        _approvedScripts.Object, _upgradePaths.Object, _verifier.Object, _signingService.Object, _unitOfWork.Object);

    private void GivenApproved(string bucket = "pm:Homebrew", string script = BashScript) =>
        _approvedScripts
            .Setup(r => r.GetAsync(Sha256, Fingerprint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApprovedScript.Create(
                Sha256, bucket, script, "Firefox", "firefox", Fingerprint, Pem, "c2ln",
                "reviewer@example.invalid", DateTimeOffset.UnixEpoch, "commit1"));

    private UpgradePath GivenLocalRow(string platform = "pm:Homebrew", string? script = null)
    {
        var row = UpgradePath.Create(
            "Firefox", platform, UpgradePathStatus.NotFound, null, UpgradeMethod.Unknown,
            null, null, null, null, null, script);
        _upgradePaths.Setup(r => r.GetAsync("Firefox", platform, It.IsAny<CancellationToken>())).ReturnsAsync(row);
        return row;
    }

    private Task<AdoptApprovedScriptResultDto> Adopt(string platform = "pm:Homebrew") =>
        CreateHandler().Handle(new AdoptApprovedScriptCommand("Firefox", platform, Sha256, Fingerprint), CancellationToken.None);

    [Fact]
    public async Task Handle_TakesTheContentAndSignsItWithThisServersOwnKey()
    {
        GivenApproved();
        var row = GivenLocalRow();

        await Adopt();

        Assert.Equal(BashScript, row.Script);
        // The local key, never the approving server's: every agent pins one signing key at enrollment
        // and it is its own server's.
        Assert.Equal($"localsig:{BashScript}", row.ScriptSignature);
        Assert.Equal(UpgradePathStatus.Found, row.Status);
        Assert.Equal(UpgradeMethod.Script, row.Method);
        // Carried across because a Script row only patches at all when an identifier is present.
        Assert.Equal("firefox", row.ApplicationIdentifier);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RefusesToReplaceAnAlreadyApprovedRow()
    {
        GivenApproved();
        var row = GivenLocalRow(script: "#!/bin/bash\nsomething a human already signed\n");
        row.SignScript("an-existing-signature");

        // The one thing adoption must never do: agents may be executing that row right now, and
        // replacing its content from a repository on a refresh nobody watched is how a merge becomes
        // fleet-wide remote code execution.
        var ex = await Assert.ThrowsAsync<DomainException>(() => Adopt());
        Assert.Contains("already has an approved script", ex.Message);
        Assert.Equal("an-existing-signature", row.ScriptSignature);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RefusesWhenTheApprovedBucketRunsADifferentInterpreter()
    {
        // A bash script approved for Homebrew, being put on a Windows row. Both sides are internally
        // consistent; only comparing them through ScriptLanguages.For catches it.
        GivenApproved(bucket: "pm:Homebrew");
        var row = GivenLocalRow(platform: PlatformBucket.Windows);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Adopt(PlatformBucket.Windows));

        Assert.Contains("runs Bash", ex.Message);
        Assert.Contains("runs PowerShell", ex.Message);
        Assert.Null(row.Script);
    }

    [Fact]
    public async Task Handle_RefusesWhenTheStoredSignatureNoLongerVerifies()
    {
        GivenApproved();
        var row = GivenLocalRow();
        _verifier.Setup(v => v.Verify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        // Re-checked at adoption rather than trusted from import time: the stored row is the one about
        // to be executed, and this is the only evidence its script text hasn't been altered in this
        // database since it was read.
        var ex = await Assert.ThrowsAsync<DomainException>(() => Adopt());
        Assert.Contains("no longer verifies", ex.Message);
        Assert.Null(row.ScriptSignature);
    }

    [Fact]
    public async Task Handle_WhenTheApprovedScriptHasNotBeenImported_Throws()
    {
        _approvedScripts
            .Setup(r => r.GetAsync(Sha256, Fingerprint, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApprovedScript?)null);
        GivenLocalRow();

        await Assert.ThrowsAsync<NotFoundException>(() => Adopt());
    }

    [Fact]
    public async Task Handle_WhenThereIsNoLocalRowForThatApplication_Throws()
    {
        GivenApproved();
        _upgradePaths
            .Setup(r => r.GetAsync("Firefox", "pm:Homebrew", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UpgradePath?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => Adopt());
    }

    [Fact]
    public async Task Handle_KeepsALocalApplicationIdentifierRatherThanTheApprovingServersNote()
    {
        GivenApproved();
        var row = UpgradePath.Create(
            "Firefox", "pm:Homebrew", UpgradePathStatus.NotFound, null, UpgradeMethod.Unknown,
            null, null, null, null, null, null, "locally-reported-id");
        _upgradePaths.Setup(r => r.GetAsync("Firefox", "pm:Homebrew", It.IsAny<CancellationToken>())).ReturnsAsync(row);

        await Adopt();

        // A local identifier came from an agent actually reporting that installation, which is better
        // evidence than the approving server's note of what it happened to be reviewing.
        Assert.Equal("locally-reported-id", row.ApplicationIdentifier);
    }
}
