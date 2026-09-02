using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.TakeServerWrittenScript;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.UpgradePaths;

/// <summary>
/// The deliberate half of a decision that used to happen by itself. A signed row keeps its reviewed
/// script across a server upgrade, so when an edit to a <c>*UpgradeScript.Build</c> body means this
/// build would write something different, a human presses this to take it — and the row lands
/// unsigned, because the new text must not reach a host before somebody has read it.
/// </summary>
public class TakeServerWrittenScriptCommandHandlerTests
{
    private readonly Mock<IUpgradePathRepository> _upgradePaths = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private static readonly string HomebrewBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Homebrew);

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

        _upgradePaths
            .Setup(r => r.GetAsync(applicationName, platform, It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        return row;
    }

    [Fact]
    public async Task Handle_ReplacesASignedRowsScript_AndLeavesItAwaitingReview()
    {
        var row = SetUpRow("firefox", HomebrewBucket, "#!/bin/bash\n# an older revision\n", "signed:reviewed-the-old-text");

        var result = await CreateHandler().Handle(
            new TakeServerWrittenScriptCommand("firefox", HomebrewBucket), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(HomebrewUpgradeScript.Build(isSelfUpdate: false), row.Script);
        // Unsigned, so is_patchable refuses it until a human signs — which is the whole reason this
        // is a button rather than something the next inventory report does.
        Assert.Null(row.ScriptSignature);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ForTheManagersOwnRow_TakesTheSelfUpdateScript()
    {
        // A manager is its own manager, so its self-update row sits in the same bucket as everything
        // it manages and is told apart by name — the same rule the scan planner uses. Getting this
        // backwards would put `brew upgrade "$APP_NAME"` on Homebrew's own row.
        var row = SetUpRow(PackageManagerCatalog.Homebrew, HomebrewBucket, "#!/bin/bash\n# an older revision\n", "signed:old");

        await CreateHandler().Handle(
            new TakeServerWrittenScriptCommand(PackageManagerCatalog.Homebrew, HomebrewBucket), CancellationToken.None);

        Assert.Equal(HomebrewUpgradeScript.Build(isSelfUpdate: true), row.Script);
    }

    [Fact]
    public async Task Handle_WhenTheRowAlreadyHoldsThisBuildsScript_ChangesNothing()
    {
        var row = SetUpRow(
            "firefox", HomebrewBucket, HomebrewUpgradeScript.Build(isSelfUpdate: false), "signed:already-current");

        var result = await CreateHandler().Handle(
            new TakeServerWrittenScriptCommand("firefox", HomebrewBucket), CancellationToken.None);

        // Pressing it twice is not an error, and must not throw away the signature on content that
        // is already exactly right.
        Assert.False(result.Changed);
        Assert.Equal("signed:already-current", row.ScriptSignature);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ForAnAiResearchedRow_Refuses()
    {
        // This server writes no script for an OS bucket, so there is no newer server-written version
        // to take — re-researching one is the Applications page's job, and silently doing nothing
        // here would look like the button was broken.
        SetUpRow("Nextcloud", PlatformBucket.MacOs, "#!/bin/bash\n# AI-authored\n", "signed:reviewed");

        var error = await Assert.ThrowsAsync<DomainException>(() => CreateHandler().Handle(
            new TakeServerWrittenScriptCommand("Nextcloud", PlatformBucket.MacOs), CancellationToken.None));

        Assert.Contains("not a recognized package manager", error.Message);
    }

    [Fact]
    public async Task Handle_WhenThereIsNoSuchRow_ReportsNotFound()
    {
        _upgradePaths
            .Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UpgradePath?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(
            new TakeServerWrittenScriptCommand("firefox", HomebrewBucket), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_ResolvesTheScriptFromTheRow_NotFromTheRequest()
    {
        // There is no parameter here that could put a bash script on a Windows row: the content
        // comes from the bucket the stored row is in. That is the failure the per-manager buckets
        // exist to prevent, so it is worth asserting the shape rather than trusting it.
        var wingetBucket = PlatformBucket.ForPackageManager(PackageManagerCatalog.Winget);
        var row = SetUpRow("Mozilla.Firefox", wingetBucket, "# an older revision\n", "signed:old");

        await CreateHandler().Handle(
            new TakeServerWrittenScriptCommand("Mozilla.Firefox", wingetBucket), CancellationToken.None);

        Assert.Equal(WingetUpgradeScript.Build(isSelfUpdate: false), row.Script);
        Assert.DoesNotContain("#!/bin/bash", row.Script);
    }
}
