using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.RemoteControl.Commands.MarkRemoteControlSessionStarted;

public class MarkRemoteControlSessionStartedCommandHandler : IRequestHandler<MarkRemoteControlSessionStartedCommand, Unit>
{
    private readonly IRemoteControlSessionRepository _sessionRepository;
    private readonly IUnitOfWork _unitOfWork;

    public MarkRemoteControlSessionStartedCommandHandler(IRemoteControlSessionRepository sessionRepository, IUnitOfWork unitOfWork)
    {
        _sessionRepository = sessionRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(MarkRemoteControlSessionStartedCommand request, CancellationToken cancellationToken)
    {
        var session = await _sessionRepository.GetByIdAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return Unit.Value;
        }

        session.MarkStarted();
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
