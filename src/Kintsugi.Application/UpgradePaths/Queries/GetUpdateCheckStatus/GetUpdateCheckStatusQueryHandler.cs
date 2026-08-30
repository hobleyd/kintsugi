using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpdateCheckStatus;

public class GetUpdateCheckStatusQueryHandler : IRequestHandler<GetUpdateCheckStatusQuery, UpdateCheckStatusDto>
{
    private readonly IUpdateCheckCoordinator _coordinator;

    public GetUpdateCheckStatusQueryHandler(IUpdateCheckCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task<UpdateCheckStatusDto> Handle(GetUpdateCheckStatusQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(_coordinator.GetStatus());
}
