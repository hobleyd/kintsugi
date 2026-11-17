using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths.Commands.CheckApplicationUpdate;

public class CheckApplicationUpdateCommandHandler : IRequestHandler<CheckApplicationUpdateCommand, CheckApplicationUpdateResult>
{
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IUpgradePathResearchClient _researchClient;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IManagedApplicationRepository _managedApplicationRepository;

    public CheckApplicationUpdateCommandHandler(
        IUpgradePathRepository upgradePathRepository,
        IUpgradePathResearchClient researchClient,
        IUnitOfWork unitOfWork,
        IManagedApplicationRepository managedApplicationRepository)
    {
        _upgradePathRepository = upgradePathRepository;
        _researchClient = researchClient;
        _unitOfWork = unitOfWork;
        _managedApplicationRepository = managedApplicationRepository;
    }

    public async Task<CheckApplicationUpdateResult> Handle(CheckApplicationUpdateCommand request, CancellationToken cancellationToken)
    {
        // Another tool patches this one, so there is nothing for Kintsugi to check: it is neither
        // failed nor unchanged, it is not Kintsugi's, which the run counts with the rows that had
        // nothing to check.
        var managed = await _managedApplicationRepository.FindAsync(request.ApplicationName, cancellationToken);
        if (managed is not null)
        {
            return new CheckApplicationUpdateResult(
                request.ApplicationName, request.Platform, false, false, $"Managed by {managed.Owner}; Kintsugi does not check it.", Skipped: true);
        }

        var existing = await _upgradePathRepository.GetAsync(request.ApplicationName, request.Platform, cancellationToken);
        if (existing is null || existing.Method != UpgradeMethod.Script
            || string.IsNullOrWhiteSpace(existing.Script) || string.IsNullOrWhiteSpace(existing.ApplicationIdentifier))
        {
            // Skipped, not failed: there is no script to run here (or no identifier to run one
            // with), which is a state a row can legitimately sit in rather than something that
            // went wrong. Counting it as a failure is what made a fleet-wide run report "8 failed"
            // against a table in which no row wore "Check Failed" — that badge comes from
            // UpgradePathStatus.Failed, which only the AI scan ever writes.
            return new CheckApplicationUpdateResult(request.ApplicationName, request.Platform, false, false, "No update script to check.", Skipped: true);
        }

        // Skipped for the same reason: an unreviewed script is a state a row sits in, and the server
        // runs only reviewed scripts (htw-secops/kintsugi#8). CheckScriptVersionAsync enforces that
        // itself; this only keeps "Check for Updates" from counting every unsigned row as failed.
        if (existing.ScriptSignature is null
            && existing.Script != PackageManagerCatalog.CurrentScriptFor(existing.ApplicationName, existing.Platform))
        {
            return new CheckApplicationUpdateResult(request.ApplicationName, request.Platform, false, false, "Not signed yet; the server runs a script only after it has been reviewed.", Skipped: true);
        }

        try
        {
            var previousVersion = existing.LatestVersion;
            var discovered = await _researchClient.CheckScriptVersionAsync(existing.Script, existing.ScriptSignature, existing.Platform,existing.ApplicationName, existing.ApplicationIdentifier, cancellationToken);
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
