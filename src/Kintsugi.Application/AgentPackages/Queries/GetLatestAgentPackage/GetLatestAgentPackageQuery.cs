using MediatR;

namespace Kintsugi.Application.AgentPackages.Queries.GetLatestAgentPackage;

public record GetLatestAgentPackageQuery(string Platform) : IRequest<AgentPackageDto?>;
