using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradeStatuses;

public class GetUpgradeStatusesQueryHandler : IRequestHandler<GetUpgradeStatusesQuery, IReadOnlyList<UpgradeStatusDto>>
{
    private readonly IUpgradePathRepository _repository;

    public GetUpgradeStatusesQueryHandler(IUpgradePathRepository repository)
    {
        _repository = repository;
    }

    public Task<IReadOnlyList<UpgradeStatusDto>> Handle(GetUpgradeStatusesQuery request, CancellationToken cancellationToken) =>
        _repository.GetStatusesAsync(request.SerialNumber, cancellationToken);
}
