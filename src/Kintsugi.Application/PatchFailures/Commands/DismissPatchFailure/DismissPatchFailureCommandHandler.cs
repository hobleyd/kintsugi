using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.PatchFailures.Commands.DismissPatchFailure;

public class DismissPatchFailureCommandHandler : IRequestHandler<DismissPatchFailureCommand, Unit>
{
    private readonly IPatchFailureRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IHostEventRecorder _events;

    public DismissPatchFailureCommandHandler(IPatchFailureRepository repository, IUnitOfWork unitOfWork, IHostEventRecorder events)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _events = events;
    }

    public async Task<Unit> Handle(DismissPatchFailureCommand request, CancellationToken cancellationToken)
    {
        var failure = await _repository.GetByIdAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"No patch failure was found with id '{request.Id}'.");

        failure.Resolve(PatchFailureResolution.Dismissed);
        await _events.FailureDismissedAsync(
            failure.HostId, failure.ApplicationName, request.DismissedBy ?? "an administrator", DateTimeOffset.UtcNow, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
