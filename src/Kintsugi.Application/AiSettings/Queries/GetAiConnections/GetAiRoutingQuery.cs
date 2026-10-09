using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Queries.GetAiConnections;

public record GetAiRoutingQuery : IRequest<AiRoutingDto>;

public class GetAiRoutingQueryHandler : IRequestHandler<GetAiRoutingQuery, AiRoutingDto>
{
    private readonly IAiConnectionRepository _connections;
    private readonly IAiFeatureRouteRepository _routes;

    public GetAiRoutingQueryHandler(IAiConnectionRepository connections, IAiFeatureRouteRepository routes)
    {
        _connections = connections;
        _routes = routes;
    }

    public async Task<AiRoutingDto> Handle(GetAiRoutingQuery request, CancellationToken cancellationToken) => new(
        (await _connections.GetAllAsync(cancellationToken)).OrderBy(c => c.Name).Select(AiConnectionDto.FromEntity).ToList(),
        (await _routes.GetAllAsync(cancellationToken)).OrderBy(r => r.Feature).Select(AiFeatureRouteDto.FromEntity).ToList());
}
