using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Commands.StartUpgradePathScan;

public class StartUpgradePathScanCommandHandler : IRequestHandler<StartUpgradePathScanCommand, StartUpgradePathScanResult>
{
    private readonly IUpgradePathScanCoordinator _coordinator;

    public StartUpgradePathScanCommandHandler(IUpgradePathScanCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task<StartUpgradePathScanResult> Handle(StartUpgradePathScanCommand request, CancellationToken cancellationToken)
    {
        var started = _coordinator.TryRequestStart();
        return Task.FromResult(new StartUpgradePathScanResult(started, _coordinator.GetStatus()));
    }
}
