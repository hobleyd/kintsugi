using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScanResults;

public class GetUpgradePathScanResultsQueryHandler : IRequestHandler<GetUpgradePathScanResultsQuery, UpgradePathRunResultsDto>
{
    private readonly IUpgradePathScanCoordinator _coordinator;

    public GetUpgradePathScanResultsQueryHandler(IUpgradePathScanCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task<UpgradePathRunResultsDto> Handle(GetUpgradePathScanResultsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(_coordinator.GetResults());
}
