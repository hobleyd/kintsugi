using MediatR;

namespace Kintsugi.Application.Hosts.Queries.GetHostById;

public record GetHostByIdQuery(Guid Id) : IRequest<HostDto?>;
