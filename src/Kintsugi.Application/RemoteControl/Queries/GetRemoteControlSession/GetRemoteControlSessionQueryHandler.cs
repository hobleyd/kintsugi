using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.RemoteControl.Queries.GetRemoteControlSession;

public class GetRemoteControlSessionQueryHandler : IRequestHandler<GetRemoteControlSessionQuery, RemoteControlSessionDto?>
{
    private readonly IRemoteControlSessionRepository _sessionRepository;
    private readonly IRemoteControlSessionBroker _broker;
    private readonly IUnitOfWork _unitOfWork;

    public GetRemoteControlSessionQueryHandler(
        IRemoteControlSessionRepository sessionRepository,
        IRemoteControlSessionBroker broker,
        IUnitOfWork unitOfWork)
    {
        _sessionRepository = sessionRepository;
        _broker = broker;
        _unitOfWork = unitOfWork;
    }

    public async Task<RemoteControlSessionDto?> Handle(GetRemoteControlSessionQuery request, CancellationToken cancellationToken)
    {
        var session = await _sessionRepository.GetByIdAsync(request.Id, cancellationToken);
        if (session is null)
        {
            return null;
        }

        // A query that writes, which needs justifying. The consent dialog timing out is the one
        // outcome no socket message ever announces — the agent stops mentioning a request it gave
        // up on, so nothing else is going to come along and close the record out. The browser is
        // polling this route for exactly as long as that dialog is up, so this is where the
        // timeout is learned and therefore where it gets recorded. Grants and refusals are not
        // written here; they arrive over the agent's control socket and are written from there,
        // whether anyone is still watching the page or not.
        if (session.Consent == RemoteControlConsent.Pending)
        {
            var live = _broker.GetConsent(session.Id);
            if (live == RemoteControlConsent.TimedOut)
            {
                session.RecordConsent(live);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }

        return RemoteControlSessionDto.From(session, _broker.IsActive(session.Id));
    }
}
