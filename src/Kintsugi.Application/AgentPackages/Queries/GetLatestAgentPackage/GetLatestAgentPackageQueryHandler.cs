using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AgentPackages.Queries.GetLatestAgentPackage;

public class GetLatestAgentPackageQueryHandler : IRequestHandler<GetLatestAgentPackageQuery, AgentPackageDto?>
{
    private readonly IAgentPackageRepository _repository;

    public GetLatestAgentPackageQueryHandler(IAgentPackageRepository repository)
    {
        _repository = repository;
    }

    public async Task<AgentPackageDto?> Handle(GetLatestAgentPackageQuery request, CancellationToken cancellationToken)
    {
        var platform = request.Platform.Trim().ToLowerInvariant();
        var package = await _repository.GetLatestByPlatformAsync(platform, cancellationToken);
        return package is null ? null : AgentPackageDto.FromEntity(package);
    }
}
