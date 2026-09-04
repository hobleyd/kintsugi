using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.RemoteControl.Commands.EndRemoteControlSession;

public class EndRemoteControlSessionCommandHandler : IRequestHandler<EndRemoteControlSessionCommand, Unit>
{
    private readonly IRemoteControlSessionRepository _sessionRepository;
    private readonly IRemoteControlSessionBroker _broker;
    private readonly IUnitOfWork _unitOfWork;

    public EndRemoteControlSessionCommandHandler(
        IRemoteControlSessionRepository sessionRepository,
        IRemoteControlSessionBroker broker,
        IUnitOfWork unitOfWork)
    {
        _sessionRepository = sessionRepository;
        _broker = broker;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(EndRemoteControlSessionCommand request, CancellationToken cancellationToken)
    {
        // Tear the sockets down first, and unconditionally: this is the call that actually stops
        // the target's screen being captured, so it must not be skipped because the row turned out
        // to be missing or already closed.
        _broker.EndSession(request.SessionId, request.Reason);

        var session = await _sessionRepository.GetByIdAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return Unit.Value;
        }

        session.MarkEnded(request.Reason);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
