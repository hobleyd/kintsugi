using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths.Commands.ReportDiscoveredVersion;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class ReportDiscoveredVersionCommandHandlerTests
{
    private readonly Mock<IUpgradePathRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private ReportDiscoveredVersionCommandHandler CreateHandler() => new(_repository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_WhenTheUpgradePathStillExists_UpdatesItsLatestVersion()
    {
        var existing = UpgradePath.Create("Firefox", "macOS", UpgradePathStatus.Found, "128.0", UpgradeMethod.Script, null, null, null, null, null);
        _repository.Setup(r => r.GetAsync("Firefox", "macOS", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        await CreateHandler().Handle(new ReportDiscoveredVersionCommand("Firefox", "macOS", "129.0"), CancellationToken.None);

        Assert.Equal("129.0", existing.LatestVersion);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenTheUpgradePathNoLongerExists_DoesNothingRatherThanFailing()
    {
        // A stale report from an agent whose upgrade path was deleted/renamed server-side since it
        // last fetched one is not an error condition.
        _repository.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);

        await CreateHandler().Handle(new ReportDiscoveredVersionCommand("Ghost", "macOS", "1.0"), CancellationToken.None);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
