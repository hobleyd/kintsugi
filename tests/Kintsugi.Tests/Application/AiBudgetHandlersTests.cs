using Moq;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.AiSettings.Commands.UpdateAiBudget;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.StartUpgradePathScan;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScanStatus;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application;

public class AiBudgetHandlersTests
{
    private static AiBudgetStatusDto Blocked() => new(50m, 50m, 100, true, true, true, "AI budget reached: $50.00 of $50.00 ...");

    private static AiBudgetStatusDto Fine() => new(5m, 50m, 10, false, false, false, null);

    private static UpgradePathScanStatusDto Idle() => new(false, 0, 0, 0, 0, 0, 0, null, null, null, []);

    [Fact]
    public async Task StartingAScanAtTheLimitIsRefusedBeforeAnythingStarts()
    {
        var coordinator = new Mock<IUpgradePathScanCoordinator>();
        var budget = new Mock<IAiBudgetService>();
        budget.Setup(b => b.EnsureWithinBudgetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new AiBudgetReachedException("AI budget reached"));

        await Assert.ThrowsAsync<AiBudgetReachedException>(() =>
            new StartUpgradePathScanCommandHandler(coordinator.Object, budget.Object).Handle(new StartUpgradePathScanCommand(), CancellationToken.None));

        coordinator.Verify(c => c.TryRequestStart(), Times.Never);
    }

    [Fact]
    public async Task StartingAScanUnderTheLimitStartsIt()
    {
        var coordinator = new Mock<IUpgradePathScanCoordinator>();
        coordinator.Setup(c => c.TryRequestStart()).Returns(true);
        coordinator.Setup(c => c.GetStatus()).Returns(Idle());

        var result = await new StartUpgradePathScanCommandHandler(coordinator.Object, Mock.Of<IAiBudgetService>())
            .Handle(new StartUpgradePathScanCommand(), CancellationToken.None);

        Assert.True(result.Started);
    }

    [Fact]
    public async Task TheScanStatusCarriesTheBudgetMessageOnlyWhileBlocked()
    {
        var coordinator = new Mock<IUpgradePathScanCoordinator>();
        coordinator.Setup(c => c.GetStatus()).Returns(Idle());
        var budget = new Mock<IAiBudgetService>();

        budget.Setup(b => b.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Blocked());
        var blocked = await new GetUpgradePathScanStatusQueryHandler(coordinator.Object, budget.Object)
            .Handle(new GetUpgradePathScanStatusQuery(), CancellationToken.None);
        Assert.StartsWith("AI budget reached", blocked.AiBudgetMessage);

        budget.Setup(b => b.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Fine());
        var fine = await new GetUpgradePathScanStatusQueryHandler(coordinator.Object, budget.Object)
            .Handle(new GetUpgradePathScanStatusQuery(), CancellationToken.None);
        Assert.Null(fine.AiBudgetMessage);
    }

    [Fact]
    public async Task SavingTheBudgetCreatesTheRowTheFirstTimeAndUpdatesItAfter()
    {
        var repository = new Mock<IAiUsageRepository>();
        AiBudgetSettings? stored = null;
        repository.Setup(r => r.GetBudgetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => stored);
        repository.Setup(r => r.AddBudgetAsync(It.IsAny<AiBudgetSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AiBudgetSettings, CancellationToken>((s, _) => stored = s).Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        var handler = new UpdateAiBudgetCommandHandler(repository.Object, unitOfWork.Object);

        var first = await handler.Handle(new UpdateAiBudgetCommand(50m, "USD", 1.55m, 80, true), CancellationToken.None);
        var second = await handler.Handle(new UpdateAiBudgetCommand(120m, "AUD", 1.6m, 70, false), CancellationToken.None);

        Assert.Equal(50m, first.BudgetAmount);
        Assert.Equal((120m, "AUD", 1.6m, 70, false), (second.BudgetAmount, second.BudgetCurrency, second.UsdToAudRate, second.WarnPercent, second.StopAtLimit));
        repository.Verify(r => r.AddBudgetAsync(It.IsAny<AiBudgetSettings>(), It.IsAny<CancellationToken>()), Times.Once);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(0.0, "USD", 1.55, 80, false)]
    [InlineData(10.0, "EUR", 1.55, 80, false)]
    [InlineData(10.0, "USD", 0.0, 80, false)]
    [InlineData(10.0, "USD", 1.55, 100, false)]
    [InlineData(10.0, "AUD", 1.55, 80, true)]
    [InlineData(null, "USD", 1.55, 80, true)]
    [InlineData(0.004, "USD", 1.55, 80, false)]
    [InlineData(10.123, "AUD", 1.55, 80, false)]
    [InlineData(0.01, "USD", 1.55, 80, true)]
    public void TheCommandIsValidatedBeforeItReachesTheDomain(double? amount, string currency, double rate, int warn, bool valid)
    {
        var result = new UpdateAiBudgetCommandValidator().Validate(
            new UpdateAiBudgetCommand((decimal?)amount, currency, (decimal)rate, warn, true));

        Assert.Equal(valid, result.IsValid);
    }
}
