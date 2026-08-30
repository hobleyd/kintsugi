using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Commands.StartUpgradePathRefresh;

public class StartUpgradePathRefreshCommandHandler : IRequestHandler<StartUpgradePathRefreshCommand, StartUpgradePathRefreshResult>
{
    private readonly IUpgradePathRefreshCoordinator _coordinator;

    public StartUpgradePathRefreshCommandHandler(IUpgradePathRefreshCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task<StartUpgradePathRefreshResult> Handle(StartUpgradePathRefreshCommand request, CancellationToken cancellationToken)
    {
        var started = _coordinator.TryStart(request.ApplicationName, request.Platform, request.PromptOverride);
        return Task.FromResult(new StartUpgradePathRefreshResult(started, _coordinator.GetStatus(request.ApplicationName)));
    }
}
