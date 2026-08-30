using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Deployments.Queries.GetDeployments;

public class GetDeploymentsQueryHandler : IRequestHandler<GetDeploymentsQuery, IReadOnlyList<PatchDeploymentDto>>
{
    private readonly IPatchDeploymentRepository _deploymentRepository;

    public GetDeploymentsQueryHandler(IPatchDeploymentRepository deploymentRepository)
    {
        _deploymentRepository = deploymentRepository;
    }

    public async Task<IReadOnlyList<PatchDeploymentDto>> Handle(GetDeploymentsQuery request, CancellationToken cancellationToken)
    {
        var deployments = await _deploymentRepository.GetAllAsync(cancellationToken);
        return deployments.Select(PatchDeploymentDto.FromEntity).ToList();
    }
}
