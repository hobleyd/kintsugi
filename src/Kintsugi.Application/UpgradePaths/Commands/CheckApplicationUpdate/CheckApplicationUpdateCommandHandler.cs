using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths.Commands.CheckApplicationUpdate;

public class CheckApplicationUpdateCommandHandler : IRequestHandler<CheckApplicationUpdateCommand, CheckApplicationUpdateResult>
{
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IUpgradePathResearchClient _researchClient;
    private readonly IUnitOfWork _unitOfWork;

    public CheckApplicationUpdateCommandHandler(
        IUpgradePathRepository upgradePathRepository,
        IUpgradePathResearchClient researchClient,
        IUnitOfWork unitOfWork)
    {
        _upgradePathRepository = upgradePathRepository;
        _researchClient = researchClient;
        _unitOfWork = unitOfWork;
    }

    public async Task<CheckApplicationUpdateResult> Handle(CheckApplicationUpdateCommand request, CancellationToken cancellationToken)
    {
        var existing = await _upgradePathRepository.GetAsync(request.ApplicationName, request.Platform, cancellationToken);
        if (existing is null || existing.Method != UpgradeMethod.Script
            || string.IsNullOrWhiteSpace(existing.Script) || string.IsNullOrWhiteSpace(existing.ApplicationIdentifier))
        {
            return new CheckApplicationUpdateResult(request.ApplicationName, request.Platform, false, false, "No update script to check.");
        }

        try
        {
            var previousVersion = existing.LatestVersion;
            var discovered = await _researchClient.CheckScriptVersionAsync(existing.Script, existing.ApplicationName, existing.ApplicationIdentifier, cancellationToken);
            if (discovered is null)
            {
                // The script itself may have broken (e.g. the vendor changed how it distributes
                // releases) — left as-is rather than regenerated here, since "Check for Updates"
                // never calls the AI; regenerating is what the per-row "Send to AI" refresh (or a
                // fresh "Find Upgrade Paths" pass once this row's status is reset) is for.
                return new CheckApplicationUpdateResult(request.ApplicationName, request.Platform, false, false, "The script did not report a version.");
            }

            existing.UpdateDiscoveredLatestVersion(discovered);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var changed = !string.Equals(previousVersion, existing.LatestVersion, StringComparison.OrdinalIgnoreCase);
            return new CheckApplicationUpdateResult(request.ApplicationName, request.Platform, true, changed, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CheckApplicationUpdateResult(request.ApplicationName, request.Platform, false, false, $"Unexpected error: {ex.Message}");
        }
    }
}
