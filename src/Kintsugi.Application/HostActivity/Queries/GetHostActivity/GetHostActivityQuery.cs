using MediatR;

namespace Kintsugi.Application.HostActivity.Queries.GetHostActivity;

public record GetHostActivityQuery(Guid HostId) : IRequest<HostActivityDto>;
