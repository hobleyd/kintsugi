using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.RemoteControl.Commands.RequestRemoteControlSession;

public class RequestRemoteControlSessionCommandHandler : IRequestHandler<RequestRemoteControlSessionCommand, RemoteControlSessionDto>
{
    private readonly IHostRepository _hostRepository;
    private readonly IRemoteControlSessionRepository _sessionRepository;
    private readonly IRemoteControlSessionBroker _broker;
    private readonly IUnitOfWork _unitOfWork;

    public RequestRemoteControlSessionCommandHandler(
        IHostRepository hostRepository,
        IRemoteControlSessionRepository sessionRepository,
        IRemoteControlSessionBroker broker,
        IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _sessionRepository = sessionRepository;
        _broker = broker;
        _unitOfWork = unitOfWork;
    }

    public async Task<RemoteControlSessionDto> Handle(RequestRemoteControlSessionCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetByIdAsync(request.HostId, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with id '{request.HostId}'.");

        var session = RemoteControlSession.Request(host.Id, host.SerialNumber, host.Hostname, request.RequestedBy);

        // Asked before the row is saved, so there is exactly one write on the ordinary path rather
        // than a Pending row followed by an update. The window this opens is real but small and
        // one-sided: if the process died between these two statements the dialog would be answered
        // for a session with no row, which RecordRemoteControlConsentCommandHandler tolerates
        // explicitly. The reverse ordering would trade that for a stored request nobody was ever
        // asked about, which is the worse record to leave behind.
        var outcome = _broker.TryRequestConsent(session.Id, host.SerialNumber, request.RequestedBy, RemoteControlDefaults.ConsentTimeout);

        if (outcome == RemoteControlRequestOutcome.AlreadyInSession)
        {
            // No row: an attempt refused because a colleague got there first says nothing about
            // this host, and recording it would bury the requests that matter in retries.
            throw new ConflictException($"'{host.Hostname}' is already in a remote control session.");
        }

        if (outcome == RemoteControlRequestOutcome.AgentUnreachable)
        {
            session.RecordConsent(RemoteControlConsent.AgentUnreachable);
        }

        await _sessionRepository.AddAsync(session, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return RemoteControlSessionDto.From(session, isActive: false);
    }
}
