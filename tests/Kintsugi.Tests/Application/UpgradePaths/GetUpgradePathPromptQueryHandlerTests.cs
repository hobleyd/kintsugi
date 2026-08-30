using MediatR;
using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathPrompt;
using Kintsugi.Application.UpgradePaths.Queries.PrepareUpgradePathScan;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class GetUpgradePathPromptQueryHandlerTests
{
    private readonly Mock<ISender> _sender = new();
    private readonly Mock<IUpgradePathResearchClient> _researchClient = new();
    private readonly Mock<IUpgradePathRepository> _upgradePathRepository = new();

    public GetUpgradePathPromptQueryHandlerTests()
    {
        // Simplest branch to exercise: the AI isn't configured, so the handler returns right after
        // looking up ExistingResult — no need to also set up PrepareUpgradePathScanQuery's work items.
        _sender.Setup(s => s.Send(It.IsAny<PrepareUpgradePathScanQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpgradePathScanPlan(false, null, []));
    }

    private GetUpgradePathPromptQueryHandler CreateHandler() => new(_sender.Object, _researchClient.Object, _upgradePathRepository.Object);

    [Fact]
    public async Task Handle_WhenTheExistingScriptIsSigned_ReportsScriptSignedTrue()
    {
        var existing = UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\n...");
        existing.SignScript("signed:#!/bin/bash\n...");
        _upgradePathRepository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(new GetUpgradePathPromptQuery("Firefox", "macOS"), CancellationToken.None);

        Assert.NotNull(result.ExistingResult);
        Assert.True(result.ExistingResult!.ScriptSigned);
    }

    [Fact]
    public async Task Handle_WhenTheExistingScriptIsNotSigned_ReportsScriptSignedFalse()
    {
        var existing = UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\n...");
        _upgradePathRepository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(new GetUpgradePathPromptQuery("Firefox", "macOS"), CancellationToken.None);

        Assert.NotNull(result.ExistingResult);
        Assert.False(result.ExistingResult!.ScriptSigned);
    }

    // Regression coverage for a real bug: a Homebrew-managed application's work item is always
    // built under PlatformBucket.Generic ("generic"), regardless of which real OS actually reported
    // it (see PrepareUpgradePathScanQueryHandler). The Applications page's per-row panel sends
    // back whatever platform the row itself is stored under — if that row were ever persisted under
    // the real OS platform instead of "generic" (as RegisterApplicationsCommandHandler's
    // registration-time seeding used to do), this lookup would find zero matching work items and
    // wrongly report the application as not installed anywhere, even though it plainly is.
    [Fact]
    public async Task Handle_ForAPackageManagerManagedApplication_ReportsManagedByAPackageManager_NotNotInstalled()
    {
        _sender.Setup(s => s.Send(It.IsAny<PrepareUpgradePathScanQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpgradePathScanPlan(true, null, new[]
            {
                new UpgradePathWorkItem("firefox", PlatformBucket.Generic, Array.Empty<string>(), UpgradePathWorkKind.PackageManagerManaged, "Homebrew"),
            }));

        var result = await CreateHandler().Handle(new GetUpgradePathPromptQuery("firefox", PlatformBucket.Generic), CancellationToken.None);

        Assert.False(result.Available);
        Assert.Equal(PlatformBucket.Generic, result.Platform);
        Assert.Contains("managed by a package manager", result.Reason);
    }
}
