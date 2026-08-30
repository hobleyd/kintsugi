using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScanStatus;

public class GetUpgradePathScanStatusQueryHandler : IRequestHandler<GetUpgradePathScanStatusQuery, UpgradePathScanStatusDto>
{
    private readonly IUpgradePathScanCoordinator _coordinator;

    public GetUpgradePathScanStatusQueryHandler(IUpgradePathScanCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task<UpgradePathScanStatusDto> Handle(GetUpgradePathScanStatusQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(_coordinator.GetStatus());
}
