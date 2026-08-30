using MediatR;

namespace Kintsugi.Application.Deployments.Queries.GetDeployments;

public record GetDeploymentsQuery : IRequest<IReadOnlyList<PatchDeploymentDto>>;
