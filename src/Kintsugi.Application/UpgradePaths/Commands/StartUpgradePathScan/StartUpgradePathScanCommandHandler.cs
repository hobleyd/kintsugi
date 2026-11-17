using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Commands.StartUpgradePathScan;

public class StartUpgradePathScanCommandHandler : IRequestHandler<StartUpgradePathScanCommand, StartUpgradePathScanResult>
{
    private readonly IUpgradePathScanCoordinator _coordinator;
    private readonly IAiBudgetService _budget;

    public StartUpgradePathScanCommandHandler(IUpgradePathScanCoordinator coordinator, IAiBudgetService budget)
    {
        _coordinator = coordinator;
        _budget = budget;
    }

    public async Task<StartUpgradePathScanResult> Handle(StartUpgradePathScanCommand request, CancellationToken cancellationToken)
    {
        // Refused up front, with the reason, rather than started and failing on every row: a scan
        // that cannot make a single AI call is a way of filling the screen with the same error.
        await _budget.EnsureWithinBudgetAsync(cancellationToken);

        var started = _coordinator.TryRequestStart();
        return new StartUpgradePathScanResult(started, _coordinator.GetStatus());
    }
}
