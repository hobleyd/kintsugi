using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings.Commands.SetAiFeatureRoute;

public record SetAiFeatureRouteCommand(AiFeature Feature, Guid ConnectionId, string Model) : IRequest<AiFeatureRouteDto>;

public class SetAiFeatureRouteCommandValidator : AbstractValidator<SetAiFeatureRouteCommand>
{
    public SetAiFeatureRouteCommandValidator()
    {
        RuleFor(c => c.Feature).IsInEnum();
        RuleFor(c => c.ConnectionId).NotEmpty();
        RuleFor(c => c.Model).NotEmpty().MaximumLength(200);
    }
}

public class SetAiFeatureRouteCommandHandler : IRequestHandler<SetAiFeatureRouteCommand, AiFeatureRouteDto>
{
    private readonly IAiFeatureRouteRepository _routes;
    private readonly IAiConnectionRepository _connections;
    private readonly IUnitOfWork _unitOfWork;

    public SetAiFeatureRouteCommandHandler(IAiFeatureRouteRepository routes, IAiConnectionRepository connections, IUnitOfWork unitOfWork)
    {
        _routes = routes;
        _connections = connections;
        _unitOfWork = unitOfWork;
    }

    public async Task<AiFeatureRouteDto> Handle(SetAiFeatureRouteCommand request, CancellationToken cancellationToken)
    {
        _ = await _connections.GetAsync(request.ConnectionId, cancellationToken)
            ?? throw new NotFoundException($"AI connection {request.ConnectionId} was not found.");

        var route = await _routes.GetAsync(request.Feature, cancellationToken);
        if (route is null)
        {
            route = AiFeatureRoute.Create(request.Feature, request.ConnectionId, request.Model);
            await _routes.AddAsync(route, cancellationToken);
        }
        else
        {
            route.Update(request.ConnectionId, request.Model);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return AiFeatureRouteDto.FromEntity(route);
    }
}
