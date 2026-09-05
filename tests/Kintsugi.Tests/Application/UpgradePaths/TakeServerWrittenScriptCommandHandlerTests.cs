using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.TakeServerWrittenScript;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.UpgradePaths;

/// <summary>
/// The deliberate half of a decision that used to happen by itself. A signed row keeps its reviewed
/// script across a server upgrade, so when an edit to a <c>*UpgradeScript.Build</c> body means this
/// build would write something different, a human presses this to take it — and the rows land
/// unsigned, because the new text must not reach a host before somebody has read it.
///
/// Addressed by (bucket, content hash), because that is what the Upgrade Scripts page lists: one
/// package-manager script once, however many applications hold it.
/// </summary>
public class TakeServerWrittenScriptCommandHandlerTests
{
    private const string OlderRevision = "#!/bin/bash\n# an older revision\n";

    private readonly Mock<IUpgradePathRepository> _upgradePaths = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Dictionary<string, List<UpgradePath>> _rowsByPlatform = new(StringComparer.Ordinal);

    private static readonly string HomebrewBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew);
    private static readonly string FlatpakBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Flatpak);

    public TakeServerWrittenScriptCommandHandlerTests()
    {
        _upgradePaths
            .Setup(r => r.GetScriptUpgradePathsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string platform, CancellationToken _) =>
                _rowsByPlatform.TryGetValue(platform, out var rows) ? rows : new List<UpgradePath>());
    }

    private TakeServerWrittenScriptCommandHandler CreateHandler() =>
        new(_upgradePaths.Object, _unitOfWork.Object);

    private UpgradePath SetUpRow(string applicationName, string platform, string script, string? signature = null)
    {
        var row = UpgradePath.Create(
            applicationName, platform, UpgradePathStatus.Found, "1.0", UpgradeMethod.Script,
            null, null, null, null, null, script, applicationName);
        if (signature is not null)
        {
            row.SignScript(signature);
        }

        if (!_rowsByPlatform.TryGetValue(platform, out var rows))
        {
            _rowsByPlatform[platform] = rows = new List<UpgradePath>();
        }

        rows.Add(row);
        return row;
    }

    private static TakeServerWrittenScriptCommand Take(string platform, string script) =>
        new(platform, ScriptContentHash.Of(script));

    [Fact]
    public async Task Handle_ReplacesEveryRowHoldingTheScript_AndLeavesThemAwaitingReview()
    {
        // Three applications, one reviewed text. The page shows that as one entry, so pressing the
        // button has to move all three: taking the newer script for firefox alone would leave
        // Homebrew's bucket running two revisions with nothing to say which one was reviewed.
        var firefox = SetUpRow("firefox", HomebrewBucket, OlderRevision, "signed:reviewed-the-old-text");
        var slack = SetUpRow("slack", HomebrewBucket, OlderRevision, "signed:reviewed-the-old-text");
        var zoom = SetUpRow("zoom", HomebrewBucket, OlderRevision, "signed:reviewed-the-old-text");

        var result = await CreateHandler().Handle(Take(HomebrewBucket, OlderRevision), CancellationToken.None);

        Assert.Equal(3, result.Changed);
        Assert.Equal(HomebrewBucket, result.Platform);
        foreach (var row in new[] { firefox, slack, zoom })
        {
            Assert.Equal(HomebrewUpgradeScript.Build(), row.Script);
            // Unsigned, so is_patchable refuses it until a human signs — which is the whole reason
            // this is a button rather than something the next inventory report does.
            Assert.Null(row.ScriptSignature);
        }

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_LeavesRowsHoldingOtherContentAlone()
    {
        // The hash is the address. A row in the same bucket holding a different text — here, one
        // already current — is not what was pressed and must not be touched, signature included.
        var stale = SetUpRow("firefox", HomebrewBucket, OlderRevision, "signed:old");
        var current = SetUpRow(
            "slack", HomebrewBucket, HomebrewUpgradeScript.Build(), "signed:already-current");

        var result = await CreateHandler().Handle(Take(HomebrewBucket, OlderRevision), CancellationToken.None);

        Assert.Equal(1, result.Changed);
        Assert.Null(stale.ScriptSignature);
        Assert.Equal("signed:already-current", current.ScriptSignature);
    }

    [Fact]
    public async Task Handle_ForTheManagersOwnRow_TakesTheSameScriptAsItsApplications()
    {
        // A manager is its own manager, so its own row sits in the same bucket as everything it
        // manages — and since every manager now writes one text for both (the script tells the
        // manager's row apart at runtime; see RecognizedPackageManager.BuildScript), an older
        // revision held by the manager's row and by a managed application collapses into one entry
        // and both land on the same text, and so the same signature once reviewed.
        var manager = SetUpRow(PackageManagerCatalog.Flatpak, FlatpakBucket, OlderRevision, "signed:old");
        var managed = SetUpRow("org.mozilla.firefox", FlatpakBucket, OlderRevision, "signed:old");

        var result = await CreateHandler().Handle(Take(FlatpakBucket, OlderRevision), CancellationToken.None);

        Assert.Equal(2, result.Changed);
        Assert.Equal(FlatpakUpgradeScript.Build(), manager.Script);
        Assert.Equal(manager.Script, managed.Script);
    }

    [Fact]
    public async Task Handle_WhenEveryRowAlreadyHoldsThisBuildsScript_ChangesNothing()
    {
        var script = HomebrewUpgradeScript.Build();
        var row = SetUpRow("firefox", HomebrewBucket, script, "signed:already-current");

        var result = await CreateHandler().Handle(Take(HomebrewBucket, script), CancellationToken.None);

        // Pressing it twice is not an error, and must not throw away the signature on content that
        // is already exactly right.
        Assert.Equal(0, result.Changed);
        Assert.Equal("signed:already-current", row.ScriptSignature);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ForAnAiResearchedRow_Refuses()
    {
        // This server writes no script for an OS bucket, so there is no newer server-written version
        // to take — re-researching one is the Applications page's job, and silently doing nothing
        // here would look like the button was broken.
        const string aiScript = "#!/bin/bash\n# AI-authored\n";
        SetUpRow("Nextcloud", PlatformBucket.MacOs, aiScript, "signed:reviewed");

        var error = await Assert.ThrowsAsync<DomainException>(() => CreateHandler().Handle(
            Take(PlatformBucket.MacOs, aiScript), CancellationToken.None));

        Assert.Contains("not a recognized package manager", error.Message);
    }

    [Fact]
    public async Task Handle_WhenNoRowHoldsThatContent_ReportsNotFound()
    {
        // A stale page names a text that is no longer there — somebody else took it, or a report
        // rewrote an unsigned row. NotFound rather than acting on whatever replaced it.
        SetUpRow("firefox", HomebrewBucket, HomebrewUpgradeScript.Build(), "signed:current");

        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(
            Take(HomebrewBucket, OlderRevision), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_ResolvesTheScriptFromTheRow_NotFromTheRequest()
    {
        // There is no parameter here that could put a bash script on a Windows row: the content
        // comes from the bucket the stored row is in. That is the failure the per-manager buckets
        // exist to prevent, so it is worth asserting the shape rather than trusting it.
        var wingetBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget);
        const string older = "# an older revision\n";
        var row = SetUpRow("Mozilla.Firefox", wingetBucket, older, "signed:old");

        await CreateHandler().Handle(Take(wingetBucket, older), CancellationToken.None);

        Assert.Equal(WingetUpgradeScript.Build(), row.Script);
        Assert.DoesNotContain("#!/bin/bash", row.Script);
    }
}
