using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Commands.ReportDiscoveredVersion;

public class ReportDiscoveredVersionCommandHandler : IRequestHandler<ReportDiscoveredVersionCommand, Unit>
{
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ReportDiscoveredVersionCommandHandler(IUpgradePathRepository upgradePathRepository, IUnitOfWork unitOfWork)
    {
        _upgradePathRepository = upgradePathRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(ReportDiscoveredVersionCommand request, CancellationToken cancellationToken)
    {
        // No matching row is not an error — e.g. a stale report from an agent whose upgrade path
        // was deleted or renamed server-side since it last fetched one. Nothing to update.
        var existing = await _upgradePathRepository.GetAsync(request.ApplicationName, request.Platform, cancellationToken);
        if (existing is null)
        {
            return Unit.Value;
        }

        existing.UpdateDiscoveredLatestVersion(request.LatestVersion);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
