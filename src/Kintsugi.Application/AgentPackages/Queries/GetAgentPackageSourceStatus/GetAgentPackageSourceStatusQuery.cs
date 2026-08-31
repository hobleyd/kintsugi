using MediatR;

namespace Kintsugi.Application.AgentPackages.Queries.GetAgentPackageSourceStatus;

/// <summary>
/// What the upstream repository currently offers, next to what this server has already published —
/// the "check for new versions" the Clients page runs on every load.
/// </summary>
public record GetAgentPackageSourceStatusQuery : IRequest<AgentPackageSourceStatusDto>;
