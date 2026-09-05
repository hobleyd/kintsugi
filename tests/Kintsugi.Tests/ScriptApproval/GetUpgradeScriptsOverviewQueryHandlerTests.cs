using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Application.ScriptApproval.Queries.GetApprovedScripts;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.ScriptApproval;

/// <summary>
/// The local half of the Upgrade Scripts page lists <em>scripts</em>, not rows. A package-manager
/// script is byte-identical for every application the manager handles and one review covers all of
/// them, so those rows collapse into one entry named for the manager; an AI-researched row is one
/// application's script and stays its own entry.
/// </summary>
public class GetUpgradeScriptsOverviewQueryHandlerTests
{
    private readonly Mock<IScriptApprovalSourceClient> _sourceClient = new();
    private readonly Mock<IApprovedScriptRepository> _approvedScripts = new();
    private readonly Mock<IUpgradePathRepository> _upgradePaths = new();
    private readonly Mock<IArtifactSigningService> _signingService = new();
    private readonly List<UpgradePath> _rows = new();

    private static readonly string HomebrewBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew);
    private static readonly string SnapBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Snap);
    private static readonly string FlatpakBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Flatpak);

    public GetUpgradeScriptsOverviewQueryHandlerTests()
    {
        _sourceClient
            .Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptApprovalSourceStatus("acme/scripts", "main", "abc123", null));
        _approvedScripts
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ApprovedScript>());
        _upgradePaths
            .Setup(r => r.GetScriptUpgradePathsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_rows);
        _upgradePaths
            .Setup(r => r.GetRowsWithoutScriptSignatureAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _rows.Where(r => r.ScriptSignature is null).ToList());
        _signingService.Setup(s => s.GetPublicKeyFingerprint()).Returns("sha256:this-server");
    }

    private GetUpgradeScriptsOverviewQueryHandler CreateHandler() => new(
        _sourceClient.Object, FakeGitHubSettings.Provider(), _approvedScripts.Object, _upgradePaths.Object,
        _signingService.Object);

    private UpgradePath GivenRow(string applicationName, string platform, string script, string? signature = null)
    {
        var row = UpgradePath.Create(
            applicationName, platform, UpgradePathStatus.Found, "1.0", UpgradeMethod.Script,
            null, null, null, null, null, script, applicationName);
        if (signature is not null)
        {
            row.SignScript(signature);
        }

        _rows.Add(row);
        return row;
    }

    private async Task<IReadOnlyList<LocalScriptDto>> LocalScriptsAsync() =>
        (await CreateHandler().Handle(new GetUpgradeScriptsOverviewQuery(), CancellationToken.None)).LocalScripts;

    [Fact]
    public async Task PackageManagerRows_CollapseIntoOneEntryPerScript_NamedForTheManager()
    {
        var managed = HomebrewUpgradeScript.Build();
        GivenRow("firefox", HomebrewBucket, managed, "signed");
        GivenRow("slack", HomebrewBucket, managed, "signed");
        GivenRow("zoom", HomebrewBucket, managed, "signed");

        var entry = Assert.Single(await LocalScriptsAsync());

        // The label is the approval repository's for the same bytes, and never a bare application
        // name — "firefox" under pm:Homebrew says nothing a reviewer can act on, since the script it
        // holds is every other Homebrew application's too.
        Assert.Equal(ApprovedScriptIdentity.PackageManagerDisplayName(PackageManagerCatalog.Homebrew, isSelfUpdate: false), entry.ApplicationName);
        Assert.Equal(HomebrewBucket, entry.Platform);
        Assert.Equal(ScriptContentHash.Of(managed), entry.Sha256);
        Assert.Equal(3, entry.Applications);
        Assert.True(entry.Signed);
        Assert.False(entry.NewerServerScriptAvailable);
    }

    [Fact]
    public async Task TheManagersOwnRow_JoinsTheSameEntryAsItsApplications()
    {
        // Every manager writes one text for its own row and its applications (see
        // RecognizedPackageManager.BuildScript) — Flatpak's tells the two apart at runtime — so the
        // manager's own row is not a second decision and not a second entry.
        GivenRow("org.mozilla.firefox", FlatpakBucket, FlatpakUpgradeScript.Build(), "signed");
        GivenRow(PackageManagerCatalog.Flatpak, FlatpakBucket, FlatpakUpgradeScript.Build(), "signed");

        var entry = Assert.Single(await LocalScriptsAsync());

        Assert.Equal(ApprovedScriptIdentity.PackageManagerDisplayName(PackageManagerCatalog.Flatpak, isSelfUpdate: false), entry.ApplicationName);
        Assert.Equal(2, entry.Applications);
    }

    [Fact]
    public async Task TwoRevisionsOfOneManagersScript_AreTwoEntries_AndTheOlderSaysANewerExists()
    {
        // Rows signed against an earlier build's text beside rows this build wrote. The reviewer's
        // decision is per text, so they must not be folded together — and it is the older group,
        // not the bucket, that "Take newer" acts on.
        const string older = "#!/bin/bash\n# an older revision of the Homebrew script\n";
        GivenRow("firefox", HomebrewBucket, older, "signed:old");
        GivenRow("slack", HomebrewBucket, older, "signed:old");
        GivenRow("zoom", HomebrewBucket, HomebrewUpgradeScript.Build(), "signed:new");

        var entries = await LocalScriptsAsync();

        Assert.Equal(2, entries.Count);
        var stale = Assert.Single(entries, e => e.Sha256 == ScriptContentHash.Of(older));
        Assert.Equal(2, stale.Applications);
        Assert.True(stale.NewerServerScriptAvailable);
        // The bytes are no build's, so the rows decide which of the manager's two scripts this was:
        // managed applications are in it, so it is the managed one.
        Assert.Equal(ApprovedScriptIdentity.PackageManagerDisplayName(PackageManagerCatalog.Homebrew, isSelfUpdate: false), stale.ApplicationName);

        var current = Assert.Single(entries, e => e.Sha256 != ScriptContentHash.Of(older));
        Assert.False(current.NewerServerScriptAvailable);
    }

    [Fact]
    public async Task AnOlderRevisionHeldOnlyByTheManagersOwnRow_IsLabelledSelfUpdate()
    {
        const string older = "#!/bin/bash\n# an older revision of Homebrew's own upgrade\n";
        GivenRow(PackageManagerCatalog.Homebrew, HomebrewBucket, older, "signed:old");

        var entry = Assert.Single(await LocalScriptsAsync());

        Assert.Equal(ApprovedScriptIdentity.PackageManagerDisplayName(PackageManagerCatalog.Homebrew, isSelfUpdate: true), entry.ApplicationName);
        Assert.True(entry.NewerServerScriptAvailable);
    }

    [Fact]
    public async Task SignedAndUnsignedRowsHoldingTheSameScript_AreSeparateEntries()
    {
        // A half-signed set gets two honest entries rather than one with a made-up verdict — and the
        // unsigned one is what AwaitingReview counts, since those applications are not patching.
        var managed = HomebrewUpgradeScript.Build();
        GivenRow("firefox", HomebrewBucket, managed, "signed");
        GivenRow("slack", HomebrewBucket, managed);

        var overview = await CreateHandler().Handle(new GetUpgradeScriptsOverviewQuery(), CancellationToken.None);

        Assert.Equal(2, overview.LocalScripts.Count);
        Assert.Single(overview.LocalScripts, e => e.Signed && e.Applications == 1);
        Assert.Single(overview.LocalScripts, e => !e.Signed && e.Applications == 1);
        Assert.Equal(1, overview.AwaitingReview);
        // Unsigned first, so outstanding work leads the table.
        Assert.False(overview.LocalScripts[0].Signed);
    }

    [Fact]
    public async Task SnapsOneTextCoversItsOwnRowAndItsManagedOnes_InOneEntry()
    {
        // snapd is itself a snap, so Snap writes the same script for both cases (as Homebrew does) and
        // the manager's own row genuinely shares an entry with what it manages. The label is the
        // managed one — the more useful of the two for a shared entry, and the same choice
        // ApprovedScriptIdentity makes.
        var script = SnapUpgradeScript.Build();
        GivenRow(PackageManagerCatalog.Snap, SnapBucket, SnapUpgradeScript.Build(), "signed");
        GivenRow("firefox", SnapBucket, script, "signed");

        var entry = Assert.Single(await LocalScriptsAsync());

        Assert.Equal(2, entry.Applications);
        Assert.Equal(ApprovedScriptIdentity.PackageManagerDisplayName(PackageManagerCatalog.Snap, isSelfUpdate: false), entry.ApplicationName);
        Assert.False(entry.NewerServerScriptAvailable);
    }

    [Fact]
    public async Task AiResearchedRows_StayOnePerApplication_EvenWithIdenticalContent()
    {
        // An OS-bucket row is one application's script. Two applications whose AI-written scripts
        // happen to be identical are still two decisions — each is about that application.
        const string script = "#!/bin/bash\n# AI-authored\n";
        GivenRow("Nextcloud", PlatformBucket.MacOs, script);
        GivenRow("Ollama", PlatformBucket.MacOs, script);

        var entries = await LocalScriptsAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.ApplicationName == "Nextcloud" && e.Applications == 1);
        Assert.Contains(entries, e => e.ApplicationName == "Ollama" && e.Applications == 1);
        Assert.All(entries, e => Assert.False(e.NewerServerScriptAvailable));
    }
}
