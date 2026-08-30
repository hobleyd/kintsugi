using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathSummaries;

public class GetUpgradePathSummariesQueryHandler : IRequestHandler<GetUpgradePathSummariesQuery, IReadOnlyList<UpgradePathSummaryDto>>
{
    private readonly IUpgradePathRepository _repository;

    public GetUpgradePathSummariesQueryHandler(IUpgradePathRepository repository)
    {
        _repository = repository;
    }

    public Task<IReadOnlyList<UpgradePathSummaryDto>> Handle(GetUpgradePathSummariesQuery request, CancellationToken cancellationToken) =>
        _repository.GetSummariesAsync(cancellationToken);
}
