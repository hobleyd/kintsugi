using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Applications.Queries.GetApplicationSummaries;

public class GetApplicationSummariesQueryHandler : IRequestHandler<GetApplicationSummariesQuery, IReadOnlyList<ApplicationSummaryDto>>
{
    private readonly IInstalledApplicationRepository _installedApplicationRepository;

    public GetApplicationSummariesQueryHandler(IInstalledApplicationRepository installedApplicationRepository)
    {
        _installedApplicationRepository = installedApplicationRepository;
    }

    public Task<IReadOnlyList<ApplicationSummaryDto>> Handle(GetApplicationSummariesQuery request, CancellationToken cancellationToken) =>
        _installedApplicationRepository.GetSummariesAsync(cancellationToken);
}
