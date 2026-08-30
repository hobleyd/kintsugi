using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AgentPackages.Queries.GetAgentPackages;

public class GetAgentPackagesQueryHandler : IRequestHandler<GetAgentPackagesQuery, IReadOnlyList<AgentPackageDto>>
{
    private readonly IAgentPackageRepository _repository;

    public GetAgentPackagesQueryHandler(IAgentPackageRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<AgentPackageDto>> Handle(GetAgentPackagesQuery request, CancellationToken cancellationToken)
    {
        var packages = await _repository.GetLatestPerPlatformAsync(cancellationToken);
        return packages.Select(AgentPackageDto.FromEntity).ToList();
    }
}
