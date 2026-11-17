using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpdateCheckResults;

public class GetUpdateCheckResultsQueryHandler : IRequestHandler<GetUpdateCheckResultsQuery, UpgradePathRunResultsDto>
{
    private readonly IUpdateCheckCoordinator _coordinator;

    public GetUpdateCheckResultsQueryHandler(IUpdateCheckCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task<UpgradePathRunResultsDto> Handle(GetUpdateCheckResultsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(_coordinator.GetResults());
}
