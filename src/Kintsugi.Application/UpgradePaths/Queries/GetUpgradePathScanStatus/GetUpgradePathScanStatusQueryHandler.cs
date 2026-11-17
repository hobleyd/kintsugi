using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScanStatus;

public class GetUpgradePathScanStatusQueryHandler : IRequestHandler<GetUpgradePathScanStatusQuery, UpgradePathScanStatusDto>
{
    private readonly IUpgradePathScanCoordinator _coordinator;
    private readonly IAiBudgetService _budget;

    public GetUpgradePathScanStatusQueryHandler(IUpgradePathScanCoordinator coordinator, IAiBudgetService budget)
    {
        _coordinator = coordinator;
        _budget = budget;
    }

    public async Task<UpgradePathScanStatusDto> Handle(GetUpgradePathScanStatusQuery request, CancellationToken cancellationToken)
    {
        // Carried on the status the Applications screen already polls, so it can grey out "Find
        // Upgrade Paths" and say why without a second call that only an administrator may make.
        var budget = await _budget.GetStatusAsync(cancellationToken);
        return _coordinator.GetStatus() with { AiBudgetMessage = budget.Blocked ? budget.Message : null };
    }
}
