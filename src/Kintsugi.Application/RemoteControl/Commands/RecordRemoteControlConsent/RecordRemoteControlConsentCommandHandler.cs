using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.RemoteControl.Commands.RecordRemoteControlConsent;

public class RecordRemoteControlConsentCommandHandler : IRequestHandler<RecordRemoteControlConsentCommand, Unit>
{
    private readonly IRemoteControlSessionRepository _sessionRepository;
    private readonly IUnitOfWork _unitOfWork;

    public RecordRemoteControlConsentCommandHandler(IRemoteControlSessionRepository sessionRepository, IUnitOfWork unitOfWork)
    {
        _sessionRepository = sessionRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(RecordRemoteControlConsentCommand request, CancellationToken cancellationToken)
    {
        var session = await _sessionRepository.GetByIdAsync(request.SessionId, cancellationToken);

        // A consent message for a session with no row is not an error worth throwing over. It means
        // either the request never got saved (see RequestRemoteControlSessionCommandHandler's note
        // on ordering) or this API process restarted since — and in both cases the agent has
        // already shown the dialog, so there is nothing to undo and nothing useful to report back
        // over a socket the user is not looking at. The relay refuses to join sockets for a session
        // it has no live state for, so an unrecorded grant cannot turn into a session.
        if (session is null)
        {
            return Unit.Value;
        }

        session.RecordConsent(request.Outcome);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
