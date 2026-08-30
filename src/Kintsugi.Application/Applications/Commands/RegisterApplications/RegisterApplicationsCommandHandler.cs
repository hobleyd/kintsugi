using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Applications.Commands.RegisterApplications;

public class RegisterApplicationsCommandHandler : IRequestHandler<RegisterApplicationsCommand, RegisterApplicationsResult>
{
    private readonly IHostRepository _hostRepository;
    private readonly IInstalledApplicationRepository _installedApplicationRepository;
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IUnitOfWork _unitOfWork;

    public RegisterApplicationsCommandHandler(
        IHostRepository hostRepository,
        IInstalledApplicationRepository installedApplicationRepository,
        IUpgradePathRepository upgradePathRepository,
        IUnitOfWork unitOfWork)
    {
        _hostRepository = hostRepository;
        _installedApplicationRepository = installedApplicationRepository;
        _upgradePathRepository = upgradePathRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<RegisterApplicationsResult> Handle(RegisterApplicationsCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetBySerialNumberAsync(request.SerialNumber, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");

        var previouslyReported = await _installedApplicationRepository.GetByHostIdAsync(host.Id, cancellationToken);
        _installedApplicationRepository.RemoveRange(previouslyReported);

        // First pass: construct every entity (deduping repeated names) so
        // each has an assigned Id before any parent linking is attempted —
        // a child can be reported before or after its package manager.
        var entitiesByName = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
        var newlyReported = new List<InstalledApplication>();
        foreach (var entry in request.Applications)
        {
            if (entitiesByName.ContainsKey(entry.Name))
            {
                continue;
            }

            var entity = new InstalledApplication(host.Id, entry.Name, entry.Version, entry.ApplicationIdentifier);
            entitiesByName[entry.Name] = entity;
            newlyReported.Add(entity);
        }

        // Second pass: link each entry naming a PackageManager to that
        // manager's own entity, if it was included in this same report.
        // An unmatched or self-referencing PackageManager is left standalone
        // rather than rejected — the report as a whole shouldn't fail over
        // one mistagged entry.
        foreach (var entry in request.Applications)
        {
            if (string.IsNullOrEmpty(entry.PackageManager))
            {
                continue;
            }

            if (entitiesByName.TryGetValue(entry.Name, out var entity)
                && entitiesByName.TryGetValue(entry.PackageManager, out var managerEntity)
                && managerEntity != entity)
            {
                entity.SetParent(managerEntity.Id);
            }
        }

        await _installedApplicationRepository.AddRangeAsync(newlyReported, cancellationToken);

        await UpsertPackageManagerUpgradePathsAsync(request.Applications, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new RegisterApplicationsResult(host.Id, newlyReported.Count);
    }

    /// <summary>
    /// A package-manager-managed entry that reports its own <see cref="ApplicationEntry.AvailableVersion"/>
    /// (straight from that manager's catalog) is authoritative — seed or refresh that application's
    /// <see cref="UpgradePath"/> directly from it rather than waiting on a separate AI research scan
    /// to (re-)discover the same information less reliably. Stored under the same
    /// <see cref="PlatformBucket.ForPackageManager"/> bucket and <see cref="UpgradeMethod.Script"/>
    /// shape <c>ResearchApplicationUpgradePathCommandHandler</c> uses (never the real per-host OS
    /// platform, and never <see cref="UpgradeMethod.PackageManagerCommand"/>) — otherwise this row
    /// would never match the key the scan planner and the Applications page's per-row panel both
    /// look it up by, and an agent would never recognize it as patchable (see the agents' own
    /// <c>is_patchable</c>, which only trusts a signed <see cref="UpgradeMethod.Script"/> row).
    /// An entry naming a manager this system doesn't recognize is left entirely alone here: there
    /// is no script to write for it, and the scan is what resolves it to NotFound with a note.
    /// Leaves any existing <see cref="UpgradePath.ScriptSignature"/> untouched when one's already
    /// set, since the script content for a given (manager, isSelfUpdate) case never changes — an
    /// admin's prior "Sign Script" review shouldn't be silently invalidated by the next routine
    /// inventory report. A row with no signature yet (brand new, or never reviewed) inherits one
    /// automatically the moment some other row's identical script content has already been signed —
    /// a human still has to review and sign the very first script per manager, but every other
    /// application sharing that exact content never needs its own separate review.
    /// </summary>
    private async Task UpsertPackageManagerUpgradePathsAsync(IReadOnlyList<ApplicationEntry> applications, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in applications)
        {
            if (string.IsNullOrEmpty(entry.PackageManager) || string.IsNullOrEmpty(entry.AvailableVersion) || !seen.Add(entry.Name))
            {
                continue;
            }

            if (!PackageManagerCatalog.TryGet(entry.PackageManager, out var packageManager))
            {
                continue;
            }

            var platform = PlatformBucket.ForPackageManager(packageManager.Name);

            // Retires rows from either superseded shape this same application may still carry: one
            // written directly under the host's real OS platform as
            // UpgradeMethod.PackageManagerCommand (before package-manager rows moved to a fixed
            // Script shape), and one under the old shared "generic" bucket (before they moved to a
            // per-manager bucket). Left in place, either would keep winning GetSummariesAsync's
            // per-host platform lookup — checked before the package-manager fallback — and
            // permanently shadow the row this method writes now.
            var legacyRows = (await _upgradePathRepository.GetAllForApplicationAsync(entry.Name, cancellationToken))
                .Where(p => p.Platform != platform
                    && ((p.Platform != PlatformBucket.Generic && p.Method == UpgradeMethod.PackageManagerCommand)
                        || p.Platform == PlatformBucket.Generic))
                .ToList();
            foreach (var legacyRow in legacyRows)
            {
                _upgradePathRepository.Remove(legacyRow);
            }

            var script = packageManager.BuildScript(false);
            // winget and Chocolatey address a package by its id; Homebrew has none, so the package
            // name stands in. Either way this is only ever handed straight back to the script as
            // --appId — see the *UpgradeScript builders.
            var applicationIdentifier = entry.ApplicationIdentifier ?? entry.Name;
            var existing = await _upgradePathRepository.GetAsync(entry.Name, platform, cancellationToken);

            if (existing is null)
            {
                var created = UpgradePath.Create(
                    entry.Name, platform, UpgradePathStatus.Found, entry.AvailableVersion,
                    UpgradeMethod.Script, downloadUrl: null, command: null, instructions: null, sourceUrl: null, notes: null,
                    script: script, applicationIdentifier: applicationIdentifier);

                var inheritedSignature = await _upgradePathRepository.FindExistingSignatureForScriptAsync(script, cancellationToken);
                if (inheritedSignature is not null)
                {
                    created.SetSignatures(inheritedSignature, null);
                }

                await _upgradePathRepository.AddAsync(created, cancellationToken);
            }
            else
            {
                existing.Update(
                    UpgradePathStatus.Found, entry.AvailableVersion, UpgradeMethod.Script,
                    downloadUrl: null, command: null, instructions: null, sourceUrl: null, notes: null,
                    script: script, applicationIdentifier: applicationIdentifier);

                if (existing.ScriptSignature is null)
                {
                    var inheritedSignature = await _upgradePathRepository.FindExistingSignatureForScriptAsync(script, cancellationToken);
                    if (inheritedSignature is not null)
                    {
                        existing.SetSignatures(inheritedSignature, existing.CommandSignature);
                    }
                }
            }
        }
    }
}
