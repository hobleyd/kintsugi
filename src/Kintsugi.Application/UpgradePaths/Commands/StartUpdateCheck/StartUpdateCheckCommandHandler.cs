using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Commands.StartUpdateCheck;

public class StartUpdateCheckCommandHandler : IRequestHandler<StartUpdateCheckCommand, StartUpdateCheckResult>
{
    private readonly IUpdateCheckCoordinator _coordinator;

    public StartUpdateCheckCommandHandler(IUpdateCheckCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task<StartUpdateCheckResult> Handle(StartUpdateCheckCommand request, CancellationToken cancellationToken)
    {
        var started = _coordinator.TryRequestStart();
        return Task.FromResult(new StartUpdateCheckResult(started, _coordinator.GetStatus()));
    }
}
