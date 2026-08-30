using MediatR;

namespace Kintsugi.Application.Hosts.Queries.GetHosts;

public record GetHostsQuery : IRequest<IReadOnlyList<HostDto>>;
