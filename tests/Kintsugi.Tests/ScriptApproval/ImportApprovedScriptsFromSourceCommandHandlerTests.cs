using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Application.ScriptApproval.Commands.ImportApprovedScriptsFromSource;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.ScriptApproval;

public class ImportApprovedScriptsFromSourceCommandHandlerTests
{
    private const string BashScript = "#!/bin/bash\nbrew upgrade \"$2\"\n";
    private const string Pem = "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n";

    private readonly Mock<IScriptApprovalSourceClient> _sourceClient = new();
    private readonly Mock<IScriptSignatureVerifier> _verifier = new();
    private readonly Mock<IApprovedScriptRepository> _approvedScripts = new();
    private readonly Mock<IUpgradePathRepository> _upgradePaths = new();
    private readonly Mock<IArtifactSigningService> _signingService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public ImportApprovedScriptsFromSourceCommandHandlerTests()
    {
        _sourceClient
            .Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptApprovalSourceStatus("acme/scripts", "main", "abc123", null));
        _verifier.Setup(v => v.Verify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _approvedScripts
            .Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApprovedScript?)null);
        _upgradePaths
            .Setup(r => r.GetUnsignedRowsWithScriptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpgradePath>());
        _signingService.Setup(s => s.Sign(It.IsAny<string>())).Returns<string?>(c => c is null ? null : $"localsig:{c}");
    }

    private ImportApprovedScriptsFromSourceCommandHandler CreateHandler() => new(
        _sourceClient.Object, _verifier.Object, _approvedScripts.Object, _upgradePaths.Object,
        _signingService.Object, _unitOfWork.Object);

    /// <summary>A corpus of one entry whose fingerprint really is the digest of the key it carries, so
    /// the handler's own fingerprint check passes and individual tests can break one thing at a time.</summary>
    private void GivenCorpus(string script, string bucket, ScriptLanguage language, string? fingerprint = null)
    {
        var sha256 = ScriptContentHash.Of(script);
        var signature = new ApprovedScriptSignatureDocument(
            sha256, fingerprint ?? ScriptSignerFingerprint.For(Pem), Pem, "c2ln", "reviewer@example.invalid", DateTimeOffset.UnixEpoch);

        _sourceClient
            .Setup(c => c.GetCorpusAsync("abc123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovedScriptCorpusReadResult(
                new[]
                {
                    new ApprovedScriptCorpusEntry(
                        sha256,
                        new ApprovedScriptMetadataDocument(sha256, bucket, language, "Firefox", "firefox"),
                        script,
                        new[] { signature }),
                },
                Array.Empty<string>()));
    }

    [Fact]
    public async Task Handle_StoresAVerifiedEntry()
    {
        GivenCorpus(BashScript, "pm:Homebrew", ScriptLanguage.Bash);

        var result = await CreateHandler().Handle(new ImportApprovedScriptsFromSourceCommand(), CancellationToken.None);

        Assert.Equal(1, result.Imported);
        Assert.Empty(result.Rejected);
        _approvedScripts.Verify(r => r.AddAsync(
            It.Is<ApprovedScript>(a => a.Script == BashScript && a.SignerFingerprint == ScriptSignerFingerprint.For(Pem)),
            It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenTheEntrysLanguageDisagreesWithItsBucket_RejectsIt()
    {
        // The shape of the bug the shared `generic` bucket used to permit: a Windows host handed a
        // genuinely-signed #!/bin/bash script. ScriptLanguages.For is what governs execution, so an
        // entry is never allowed to assert a language that disagrees with it.
        GivenCorpus(BashScript, PlatformBucket.Windows, ScriptLanguage.Bash);

        var result = await CreateHandler().Handle(new ImportApprovedScriptsFromSourceCommand(), CancellationToken.None);

        Assert.Equal(0, result.Imported);
        Assert.Contains(result.Rejected, r => r.Contains("runs PowerShell"));
        _approvedScripts.Verify(r => r.AddAsync(It.IsAny<ApprovedScript>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenASignatureDoesNotVerify_RejectsIt()
    {
        GivenCorpus(BashScript, "pm:Homebrew", ScriptLanguage.Bash);
        _verifier.Setup(v => v.Verify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var result = await CreateHandler().Handle(new ImportApprovedScriptsFromSourceCommand(), CancellationToken.None);

        Assert.Equal(0, result.Imported);
        Assert.Contains(result.Rejected, r => r.Contains("does not verify"));
    }

    [Fact]
    public async Task Handle_WhenAnEntryClaimsAFingerprintItsKeyDoesNotHashTo_RejectsIt()
    {
        // The fingerprint is the only provenance shown to whoever decides to adopt, so an entry must
        // not be able to claim any signer it likes — including this server itself — while carrying a
        // different key.
        GivenCorpus(BashScript, "pm:Homebrew", ScriptLanguage.Bash, fingerprint: "sha256:deadbeef");

        var result = await CreateHandler().Handle(new ImportApprovedScriptsFromSourceCommand(), CancellationToken.None);

        Assert.Equal(0, result.Imported);
        Assert.Contains(result.Rejected, r => r.Contains("but its key hashes to"));
    }

    [Fact]
    public async Task Handle_BlessesALocalRowHoldingTheSameBytes_SignedWithThisServersOwnKey()
    {
        GivenCorpus(BashScript, "pm:Homebrew", ScriptLanguage.Bash);
        var local = UpgradePath.Create(
            "Firefox", "pm:Homebrew", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, BashScript);
        _upgradePaths
            .Setup(r => r.GetUnsignedRowsWithScriptAsync(BashScript, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpgradePath> { local });

        var result = await CreateHandler().Handle(new ImportApprovedScriptsFromSourceCommand(), CancellationToken.None);

        // The local key, not the approving server's: every agent pins exactly one signing key at
        // enrollment and it is its own server's, so a remote signature would be genuine and refused.
        Assert.Equal($"localsig:{BashScript}", local.ScriptSignature);
        // And no script text changed — that is what makes blessing safe to automate.
        Assert.Equal(BashScript, local.Script);
        Assert.Equal("Firefox", Assert.Single(result.Blessed).ApplicationName);
    }

    [Fact]
    public async Task Handle_DoesNotBlessALocalRowWhoseBucketRunsADifferentInterpreter()
    {
        GivenCorpus(BashScript, "pm:Homebrew", ScriptLanguage.Bash);
        // A local inconsistency — bash content filed under a PowerShell bucket. Making it executable
        // would paper over it in the most dangerous possible way.
        var local = UpgradePath.Create(
            "Firefox", PlatformBucket.Windows, UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, BashScript);
        _upgradePaths
            .Setup(r => r.GetUnsignedRowsWithScriptAsync(BashScript, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UpgradePath> { local });

        var result = await CreateHandler().Handle(new ImportApprovedScriptsFromSourceCommand(), CancellationToken.None);

        Assert.Null(local.ScriptSignature);
        Assert.Empty(result.Blessed);
        Assert.Contains(result.Rejected, r => r.Contains("left unsigned"));
    }

    [Fact]
    public async Task Handle_WhenTheUpstreamCannotBeRead_Throws()
    {
        _sourceClient
            .Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptApprovalSourceStatus("acme/scripts", null, null, "GitHub is unreachable."));

        // Nothing to import at all, so unlike a malformed individual entry this is the refresh
        // failing rather than an outcome to report.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateHandler().Handle(new ImportApprovedScriptsFromSourceCommand(), CancellationToken.None));
        Assert.Contains("unreachable", ex.Message);
    }
}
