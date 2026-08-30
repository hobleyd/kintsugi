using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathRefreshStatus;

public class GetUpgradePathRefreshStatusQueryHandler : IRequestHandler<GetUpgradePathRefreshStatusQuery, UpgradePathRefreshStatusDto>
{
    private readonly IUpgradePathRefreshCoordinator _coordinator;

    public GetUpgradePathRefreshStatusQueryHandler(IUpgradePathRefreshCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task<UpgradePathRefreshStatusDto> Handle(GetUpgradePathRefreshStatusQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(_coordinator.GetStatus(request.ApplicationName));
}
