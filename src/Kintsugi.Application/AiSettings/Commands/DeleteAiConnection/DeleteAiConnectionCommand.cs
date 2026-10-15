using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Commands.DeleteAiConnection;

public record DeleteAiConnectionCommand(Guid Id) : IRequest;

public class DeleteAiConnectionCommandHandler : IRequestHandler<DeleteAiConnectionCommand>
{
    private readonly IAiConnectionRepository _connections;
    private readonly IAiFeatureRouteRepository _routes;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteAiConnectionCommandHandler(IAiConnectionRepository connections, IAiFeatureRouteRepository routes, IUnitOfWork unitOfWork)
    {
        _connections = connections;
        _routes = routes;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(DeleteAiConnectionCommand request, CancellationToken cancellationToken)
    {
        var connection = await _connections.GetAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"AI connection {request.Id} was not found.");

        // Refused rather than cascaded: silently unrouting script research would turn the AI off
        // for the whole fleet with nothing on screen to say why.
        var using_ = (await _routes.GetAllAsync(cancellationToken)).Where(r => r.ConnectionId == request.Id).Select(r => r.Feature).ToList();
        if (using_.Count > 0)
        {
            throw new ConflictException(
                $"'{connection.Name}' is still used by {string.Join(", ", using_)}. Route those features elsewhere first.");
        }

        _connections.Remove(connection);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
