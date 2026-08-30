using MediatR;

namespace Kintsugi.Application.AgentPackages.Queries.GetAgentPackages;

public record GetAgentPackagesQuery : IRequest<IReadOnlyList<AgentPackageDto>>;
