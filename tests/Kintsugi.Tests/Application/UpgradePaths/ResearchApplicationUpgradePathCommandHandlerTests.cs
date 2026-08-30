using Moq;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.ResearchApplicationUpgradePath;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class ResearchApplicationUpgradePathCommandHandlerTests
{
    private readonly Mock<IUpgradePathRepository> _repository = new();
    private readonly Mock<IUpgradePathResearchClient> _researchClient = new();
    private readonly Mock<IArtifactSigningService> _artifactSigningService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private static readonly AiProviderSettings Settings = new(AiProvider.Anthropic, "sk-123", null, "claude-sonnet-5");

    public ResearchApplicationUpgradePathCommandHandlerTests()
    {
        _artifactSigningService.Setup(s => s.Sign(It.IsAny<string>())).Returns<string?>(content => content is null ? null : $"signed:{content}");
        _repository.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);
        // No pre-existing rows for any application by default — individual tests override this to
        // exercise the legacy-row cleanup in RetireLegacyPackageManagerRowAsync.
        _repository.Setup(r => r.GetAllForApplicationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<UpgradePath>());
        // No already-signed row anywhere with matching script content by default — individual tests
        // override this to exercise the signature-inheritance behavior.
        _repository.Setup(r => r.FindExistingSignatureForScriptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
    }

    private ResearchApplicationUpgradePathCommandHandler CreateHandler() =>
        new(_repository.Object, _researchClient.Object, _artifactSigningService.Object, _unitOfWork.Object);

    /// <summary>Where every Homebrew-managed row lives — its manager's own bucket, not an OS one
    /// and not the old shared "generic" one. See PlatformBucket.ForPackageManager.</summary>
    private static readonly string HomebrewBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew);

    // Always carries the shared, non-null Settings — a test needing to exercise the
    // Settings-is-null branch (see below) constructs a ResearchApplicationUpgradePathCommand
    // directly instead, since an optional parameter defaulting to null can't distinguish "the
    // caller wants null" from "the caller didn't say" without silently overriding an explicit null.
    private static ResearchApplicationUpgradePathCommand Command(
        UpgradePathWorkKind kind,
        string? packageManagerName = null,
        string platform = PlatformBucket.MacOs,
        string? applicationIdentifier = null,
        bool forceRecheck = false) =>
        new("Firefox", platform, Array.Empty<string>(), kind, packageManagerName, applicationIdentifier, Settings, forceRecheck);

    [Fact]
    public async Task Handle_PackageManagerManaged_ForHomebrew_ResolvesToADeterministicScript_WithoutCallingAi()
    {
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), "Firefox", "Firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync("128.0");

        var result = await CreateHandler().Handle(Command(UpgradePathWorkKind.PackageManagerManaged, "Homebrew"), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(UpgradeMethod.Script, result.Method);
        Assert.Null(result.Command);
        Assert.NotNull(result.Script);
        // The package name is never baked into the script text — it's read from --appName at
        // runtime instead (see HomebrewUpgradeScript.Build) — so this same script is exactly what
        // every other Homebrew-managed application gets too.
        Assert.DoesNotContain("Firefox", result.Script);
        Assert.Contains("APP_NAME=\"$2\"", result.Script);
        // brew update runs first so the upgrade isn't acting on a stale formula/cask index.
        Assert.Contains("brew update && brew upgrade \"$APP_NAME\"", result.Script);
        Assert.Contains("--update-version", result.Script);
        Assert.Equal("128.0", result.LatestVersion);
        _researchClient.Verify(c => c.GenerateScriptAsync(It.IsAny<AiProviderSettings>(), It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_PackageManagerManaged_ProducesTheIdenticalScript_RegardlessOfApplicationName()
    {
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.0");

        var firefoxResult = await CreateHandler().Handle(Command(UpgradePathWorkKind.PackageManagerManaged, "Homebrew"), CancellationToken.None);
        var wgetCommand = new ResearchApplicationUpgradePathCommand(
            "wget", PlatformBucket.MacOs, Array.Empty<string>(), UpgradePathWorkKind.PackageManagerManaged, "Homebrew", null, Settings);
        var wgetResult = await CreateHandler().Handle(wgetCommand, CancellationToken.None);

        // The whole point: one script, one signature, usable for every Homebrew-managed
        // application — not a separate review per app.
        Assert.Equal(firefoxResult.Script, wgetResult.Script);
    }

    [Fact]
    public async Task Handle_PackageManagerManaged_RetiresALegacyPackageManagerCommandRow_StoredUnderTheRealOsPlatform()
    {
        // Reproduces a row left over from before Homebrew moved to the fixed per-manager/Script shape:
        // stored under the real OS platform, as PackageManagerCommand. Left in place, it would keep
        // winning GetSummariesAsync's per-host platform lookup (tried before its package-manager fallback)
        // and permanently shadow the correctly-shaped row this run resolves to below — so "Find
        // Upgrade Paths" must actually retire it, not just create a second row alongside it.
        var legacyRow = UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, "127.0", UpgradeMethod.PackageManagerCommand,
            null, "brew upgrade Firefox", null, null, null);
        _repository.Setup(r => r.GetAllForApplicationAsync("Firefox", It.IsAny<CancellationToken>())).ReturnsAsync(new List<UpgradePath> { legacyRow });

        await CreateHandler().Handle(
            Command(UpgradePathWorkKind.PackageManagerManaged, "Homebrew", platform: HomebrewBucket), CancellationToken.None);

        _repository.Verify(r => r.Remove(legacyRow), Times.Once);
        _repository.Verify(r => r.AddAsync(
            It.Is<UpgradePath>(p => p.Platform == HomebrewBucket && p.Method == UpgradeMethod.Script),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PackageManagerManaged_RetiresALegacyRow_EvenWhenThePackageManagerRowIsAlreadyFoundAndSkipped()
    {
        // A second "Find Upgrade Paths" run (after a first run already created the correct
        // per-manager row, but before this cleanup existed) takes the "already Found, skip" branch — which
        // doesn't reach ApplyPackageManagerCommandAsync at all. The cleanup must still run, or the
        // legacy row would keep shadowing the correct one forever.
        _repository.Setup(r => r.GetAsync("Firefox", HomebrewBucket, It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpgradePath.Create(
                "Firefox", HomebrewBucket, UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
                null, null, null, null, null, "#!/bin/bash\n...", "Firefox"));
        var legacyRow = UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, "127.0", UpgradeMethod.PackageManagerCommand,
            null, "brew upgrade Firefox", null, null, null);
        _repository.Setup(r => r.GetAllForApplicationAsync("Firefox", It.IsAny<CancellationToken>())).ReturnsAsync(new List<UpgradePath> { legacyRow });

        var result = await CreateHandler().Handle(
            Command(UpgradePathWorkKind.PackageManagerManaged, "Homebrew", platform: HomebrewBucket), CancellationToken.None);

        Assert.True(result.Skipped);
        _repository.Verify(r => r.Remove(legacyRow), Times.Once);
    }

    [Fact]
    public async Task Handle_PackageManagerManaged_ForHomebrew_NeverSignsTheScriptAutomatically()
    {
        // No other row anywhere carries a signature over this exact script content (the default
        // FindExistingSignatureForScriptAsync setup) — nothing to inherit, so this stays unsigned.
        await CreateHandler().Handle(Command(UpgradePathWorkKind.PackageManagerManaged, "Homebrew"), CancellationToken.None);

        _repository.Verify(r => r.AddAsync(It.Is<UpgradePath>(p => p.ScriptSignature == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PackageManagerManaged_InheritsAnExistingSignature_WhenIdenticalScriptContentIsAlreadySigned()
    {
        // Some other application's Homebrew row already carries a human-reviewed signature over
        // this exact script content (they're all identical now — see HomebrewUpgradeScript.Build)
        // — this freshly-resolved row should inherit that same trust immediately, not sit unsigned
        // waiting on its own separate "Sign Script" review.
        _repository
            .Setup(r => r.FindExistingSignatureForScriptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("signed:already-reviewed-elsewhere");

        await CreateHandler().Handle(Command(UpgradePathWorkKind.PackageManagerManaged, "Homebrew"), CancellationToken.None);

        _repository.Verify(r => r.AddAsync(
            It.Is<UpgradePath>(p => p.ScriptSignature == "signed:already-reviewed-elsewhere"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AlreadyFoundScriptPath_InheritsAnExistingSignature_WhenIdenticalScriptContentIsAlreadySigned()
    {
        // A row already Found (and skipped) but never itself reviewed and signed should still
        // self-heal the moment some other row's identical script content turns out to be signed —
        // e.g. a human just signed a sibling Homebrew application sharing this exact script.
        var existing = UpgradePath.Create(
            "Firefox", HomebrewBucket, UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...", "Firefox");
        _repository.Setup(r => r.GetAsync("Firefox", HomebrewBucket, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _repository
            .Setup(r => r.FindExistingSignatureForScriptAsync("#!/bin/bash\n...", It.IsAny<CancellationToken>()))
            .ReturnsAsync("signed:already-reviewed-elsewhere");

        await CreateHandler().Handle(
            Command(UpgradePathWorkKind.PackageManagerManaged, "Homebrew", platform: HomebrewBucket), CancellationToken.None);

        Assert.Equal("signed:already-reviewed-elsewhere", existing.ScriptSignature);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PackageManagerManaged_ForAnUnrecognizedManager_ResolvesToNotFound_WithoutCallingAi()
    {
        var result = await CreateHandler().Handle(Command(UpgradePathWorkKind.PackageManagerManaged, "SomeNewManager"), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.NotFound, result.Status);
        Assert.Null(result.Command);
        Assert.Contains("SomeNewManager", result.Note);
        _researchClient.Verify(c => c.GenerateScriptAsync(It.IsAny<AiProviderSettings>(), It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_PackageManagerSelfUpdate_ForHomebrew_ResolvesToADeterministicSelfUpdateScript()
    {
        var result = await CreateHandler().Handle(Command(UpgradePathWorkKind.PackageManagerSelfUpdate, "Homebrew"), CancellationToken.None);

        Assert.Equal(UpgradeMethod.Script, result.Method);
        Assert.Null(result.Command);
        Assert.NotNull(result.Script);
        Assert.Contains("brew update && brew upgrade", result.Script);
    }

    [Fact]
    public async Task Handle_Research_OnAnUnsupportedPlatform_ResolvesToNotFound_WithoutCallingAi()
    {
        // Linux (and the catch-all "generic" OS bucket) have no prompt written for them and no way
        // to validate or run what came back, so they resolve to NotFound rather than producing a
        // script nothing can check. macOS and Windows both do have one — see the tests below.
        var result = await CreateHandler().Handle(Command(UpgradePathWorkKind.Research, platform: PlatformBucket.Linux), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.NotFound, result.Status);
        Assert.Contains("only supported on macOS and Windows", result.Note);
        _researchClient.Verify(c => c.GenerateScriptAsync(It.IsAny<AiProviderSettings>(), It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_Research_OnWindows_CallsTheAi_AndPersistsTheGeneratedScript()
    {
        _researchClient
            .Setup(c => c.GenerateScriptAsync(It.IsAny<AiProviderSettings>(), It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpgradePathScriptResult(UpgradePathStatus.Found, "Set-StrictMode -Version Latest\n...", null));
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync(It.IsAny<string>(), PlatformBucket.Windows, "Firefox", "Mozilla Firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync("129.0");

        var result = await CreateHandler().Handle(
            Command(UpgradePathWorkKind.Research, platform: PlatformBucket.Windows, applicationIdentifier: "Mozilla Firefox"),
            CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(UpgradeMethod.Script, result.Method);
        Assert.Equal("129.0", result.LatestVersion);
        // The platform is what picks the interpreter the version check runs the script under — a
        // PowerShell script handed to bash would fail every time, silently leaving LatestVersion
        // null and so making the application permanently unpatchable.
        _researchClient.Verify(
            c => c.CheckScriptVersionAsync(It.IsAny<string>(), PlatformBucket.Windows, "Firefox", "Mozilla Firefox", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_PackageManagerManaged_ForWinget_ResolvesToAPowerShellScript_AddressedByPackageId()
    {
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), "Firefox", "Mozilla.Firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync("129.0");

        var result = await CreateHandler().Handle(
            Command(UpgradePathWorkKind.PackageManagerManaged, PackageManagerCatalog.Winget,
                platform: PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget), applicationIdentifier: "Mozilla.Firefox"),
            CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(UpgradeMethod.Script, result.Method);
        Assert.NotNull(result.Script);
        Assert.Contains("winget upgrade --exact --id $AppId", result.Script);
        // Not a bash script — the whole reason package-manager rows moved to their own bucket.
        Assert.DoesNotContain("#!/bin/bash", result.Script);
        Assert.Equal("129.0", result.LatestVersion);
        _researchClient.Verify(c => c.GenerateScriptAsync(It.IsAny<AiProviderSettings>(), It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_PackageManagerManaged_ForChocolatey_ResolvesToAPowerShellScript()
    {
        var result = await CreateHandler().Handle(
            Command(UpgradePathWorkKind.PackageManagerManaged, PackageManagerCatalog.Chocolatey,
                platform: PlatformBucket.ForPackageManager(PackageManagerCatalog.Chocolatey), applicationIdentifier: "firefox"),
            CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.NotNull(result.Script);
        Assert.Contains("choco upgrade $AppId", result.Script);
        Assert.DoesNotContain("#!/bin/bash", result.Script);
    }

    [Fact]
    public async Task Handle_Research_OnMacOs_WithNoAiSettings_ResolvesToNotFound_WithoutCallingAi()
    {
        var command = new ResearchApplicationUpgradePathCommand(
            "Firefox", PlatformBucket.MacOs, Array.Empty<string>(), UpgradePathWorkKind.Research,
            PackageManagerName: null, ApplicationIdentifier: "org.mozilla.firefox", Settings: null);

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        Assert.Equal(UpgradePathStatus.NotFound, result.Status);
        Assert.Contains("AI agent is not configured", result.Note);
        _researchClient.Verify(c => c.GenerateScriptAsync(It.IsAny<AiProviderSettings>(), It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_Research_OnMacOs_WhenAiFindsAReliableMethod_PersistsTheGeneratedScriptAndChecksItsVersion()
    {
        _researchClient
            .Setup(c => c.GenerateScriptAsync(Settings, It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpgradePathScriptResult(UpgradePathStatus.Found, "#!/bin/bash\n...", null));
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync("#!/bin/bash\n...", PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync("129.0");

        var result = await CreateHandler().Handle(Command(UpgradePathWorkKind.Research, applicationIdentifier: "org.mozilla.firefox"), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(UpgradeMethod.Script, result.Method);
        Assert.Equal("129.0", result.LatestVersion);
        _repository.Verify(r => r.AddAsync(It.Is<UpgradePath>(p => p.ScriptSignature == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_Research_OnMacOs_WhenAiFindsAReliableMethod_NeverSignsTheScriptAutomatically()
    {
        // Script signing now requires a human to review the result and explicitly sign it via the
        // "Sign Script" action — a freshly AI-generated script must never come back pre-signed.
        _researchClient
            .Setup(c => c.GenerateScriptAsync(Settings, It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpgradePathScriptResult(UpgradePathStatus.Found, "#!/bin/bash\n...", null));
        _researchClient
            .Setup(c => c.CheckScriptVersionAsync("#!/bin/bash\n...", PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", It.IsAny<CancellationToken>()))
            .ReturnsAsync("129.0");

        await CreateHandler().Handle(Command(UpgradePathWorkKind.Research, applicationIdentifier: "org.mozilla.firefox"), CancellationToken.None);

        _artifactSigningService.Verify(s => s.Sign("#!/bin/bash\n..."), Times.Never);
    }

    [Fact]
    public async Task Handle_Research_OnMacOs_WhenAiFindsNoReliableMethod_PersistsNotFound()
    {
        _researchClient
            .Setup(c => c.GenerateScriptAsync(Settings, It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpgradePathScriptResult(UpgradePathStatus.NotFound, null, "No reliable update mechanism found."));

        var result = await CreateHandler().Handle(Command(UpgradePathWorkKind.Research, applicationIdentifier: "org.mozilla.firefox"), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.NotFound, result.Status);
        Assert.Equal(UpgradeMethod.Unknown, result.Method);
        _researchClient.Verify(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenTheResearchClientThrows_PersistsFailed_RatherThanPropagatingTheException()
    {
        _researchClient
            .Setup(c => c.GenerateScriptAsync(Settings, It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network is down"));

        var result = await CreateHandler().Handle(Command(UpgradePathWorkKind.Research, applicationIdentifier: "org.mozilla.firefox"), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Failed, result.Status);
        Assert.False(result.Skipped);
        Assert.Contains("network is down", result.Note);
    }

    [Fact]
    public async Task Handle_AlreadyFoundNonScriptPath_WithoutForceRecheck_IsSkippedEntirely()
    {
        var existing = UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, "1.0", UpgradeMethod.PackageManagerCommand,
            null, "brew upgrade firefox", null, null, null);
        existing.SetSignatures(null, "signed:brew upgrade firefox");
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(Command(UpgradePathWorkKind.PackageManagerManaged, "Homebrew"), CancellationToken.None);

        Assert.True(result.Skipped);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyFoundPath_WithACommandButNoCommandSignature_BackfillsTheSignatureAndSaves()
    {
        // Reproduces rows left over from before artifact signing existed (or any other row that
        // somehow ended up unsigned): the agent's is_patchable check refuses to run an unsigned
        // command, so a row like this would otherwise sit "Found" and visible on the Applications
        // page forever while never actually being patchable.
        var existing = UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, "1.0", UpgradeMethod.PackageManagerCommand,
            null, "brew upgrade firefox", null, null, null);
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(Command(UpgradePathWorkKind.PackageManagerManaged, "Homebrew"), CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.Equal("signed:brew upgrade firefox", existing.CommandSignature);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AlreadyFoundScriptPath_WithoutForceRecheck_SkipsWithoutRecheckingVersion()
    {
        // Re-checking an already-Found script's version is "Check for Updates"' job now (see
        // CheckApplicationUpdateCommandHandlerTests) — a scan just skips it, leaving LatestVersion
        // exactly as it was, and never calls CheckScriptVersionAsync or the AI.
        var existing = UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, null, null, null, null, "#!/bin/bash\n...", "org.mozilla.firefox");
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(Command(UpgradePathWorkKind.Research, applicationIdentifier: "org.mozilla.firefox"), CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.Equal("128.0", existing.LatestVersion);
        // No Command on this row, so there's nothing for the Command backfill to do. Script
        // signing has nothing to inherit either — no other row anywhere carries a signature over
        // this exact (AI-generated, so effectively unique) content — so it's left as-is, waiting on
        // a human to sign it via the "Sign Script" action.
        Assert.Null(existing.ScriptSignature);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _researchClient.Verify(c => c.CheckScriptVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _researchClient.Verify(c => c.GenerateScriptAsync(It.IsAny<AiProviderSettings>(), It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyFoundScriptPath_WithAnUnsignedScript_NeverBackfillsTheScriptSignature()
    {
        // An unsigned script only ever gets signed by a human via "Sign Script", or by inheriting a
        // signature already recorded elsewhere for byte-identical content (see
        // Handle_AlreadyFoundScriptPath_InheritsAnExistingSignature... above) — never unconditionally,
        // the way an unsigned Command self-heals. This AI-generated script's content is effectively
        // unique, so with nothing to inherit (the default FindExistingSignatureForScriptAsync setup),
        // it's correctly left unsigned here.
        var existing = UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, "128.0", UpgradeMethod.Script,
            null, "brew upgrade firefox", null, null, null, "#!/bin/bash\n...", "org.mozilla.firefox");
        existing.SetSignatures(null, null);
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        await CreateHandler().Handle(Command(UpgradePathWorkKind.Research, applicationIdentifier: "org.mozilla.firefox"), CancellationToken.None);

        Assert.Null(existing.ScriptSignature);
        Assert.Equal("signed:brew upgrade firefox", existing.CommandSignature);
        _artifactSigningService.Verify(s => s.Sign("#!/bin/bash\n..."), Times.Never);
    }

    [Fact]
    public async Task Handle_ForceRecheck_BypassesTheSkip_EvenForAnAlreadyFoundPath()
    {
        var existing = UpgradePath.Create(
            "Firefox", PlatformBucket.MacOs, UpgradePathStatus.Found, "1.0", UpgradeMethod.PackageManagerCommand,
            null, "brew upgrade firefox", null, null, null);
        _repository.Setup(r => r.GetAsync("Firefox", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(
            Command(UpgradePathWorkKind.PackageManagerManaged, "Homebrew", forceRecheck: true), CancellationToken.None);

        Assert.False(result.Skipped);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
