using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;

namespace Kintsugi.Application.LogCollections.Commands;

/// <summary>Deletes a collected log now, or cancels a request nobody has picked up. Either way it is on the device's
/// timeline with the administrator's name.</summary>
public record DeleteLogCollectionCommand(Guid HostId, Guid CollectionId, string Actor) : IRequest;

public class DeleteLogCollectionCommandHandler : IRequestHandler<DeleteLogCollectionCommand>
{
    private readonly ILogCollectionRepository _collections;
    private readonly IHostEventRecorder _events;
    private readonly IUnitOfWork _unitOfWork;

    public DeleteLogCollectionCommandHandler(ILogCollectionRepository collections, IHostEventRecorder events, IUnitOfWork unitOfWork)
    {
        _collections = collections;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(DeleteLogCollectionCommand request, CancellationToken cancellationToken)
    {
        var collection = await _collections.GetByIdAsync(request.CollectionId, cancellationToken);
        if (collection is null || collection.HostId != request.HostId)
        {
            throw new NotFoundException("No such log collection for this device.");
        }

        var now = DateTimeOffset.UtcNow;
        var what = collection.IsActiveAt(now) ? "Request cancelled" : "Collected log deleted";
        await _collections.DeleteAsync(collection, cancellationToken);
        await _events.LogsDeletedAsync(request.HostId, request.Actor, what, now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
