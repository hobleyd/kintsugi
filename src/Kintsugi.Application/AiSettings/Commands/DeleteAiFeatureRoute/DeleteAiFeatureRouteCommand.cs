using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings.Commands.DeleteAiFeatureRoute;

/// <summary>Removes a feature's own route, so it falls back down the chain (see
/// <see cref="AiProviderSettings.RouteFor"/>). Removing <see cref="AiFeature.ScriptResearch"/>'s
/// leaves Routed mode unconfigured, which the settings page says.</summary>
public record DeleteAiFeatureRouteCommand(AiFeature Feature) : IRequest;

public class DeleteAiFeatureRouteCommandHandler : IRequestHandler<DeleteAiFeatureRouteCommand>
{
    private readonly IAiFeatureRouteRepository _routes;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteAiFeatureRouteCommandHandler(IAiFeatureRouteRepository routes, IUnitOfWork unitOfWork)
    {
        _routes = routes;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(DeleteAiFeatureRouteCommand request, CancellationToken cancellationToken)
    {
        var route = await _routes.GetAsync(request.Feature, cancellationToken);
        if (route is not null)
        {
            _routes.Remove(route);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
