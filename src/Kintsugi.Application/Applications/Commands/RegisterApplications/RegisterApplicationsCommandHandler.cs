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
    /// (currently: Homebrew, straight from its catalog) is authoritative — seed or refresh that
    /// application's <see cref="UpgradePath"/> directly from it rather than waiting on a separate AI
    /// research scan to (re-)discover the same information less reliably. Stored under the same
    /// fixed <see cref="PlatformBucket.Generic"/> bucket and <see cref="UpgradeMethod.Script"/>
    /// shape <c>ResearchApplicationUpgradePathCommandHandler</c> uses for Homebrew (never
    /// the real per-host OS platform, and never <see cref="UpgradeMethod.PackageManagerCommand"/>)
    /// — otherwise this row would never match the (application, "generic") key the scan planner
    /// and the Applications page's per-row panel both look it up by, and an agent would never
    /// recognize it as patchable (see the macOS agent's own <c>is_patchable</c>, which only trusts
    /// a signed <see cref="UpgradeMethod.Script"/> row for a recognized package manager now).
    /// Leaves any existing <see cref="UpgradePath.ScriptSignature"/> untouched when one's already
    /// set, since the script content for a given isSelfUpdate case never changes — an admin's prior
    /// "Sign Script" review shouldn't be silently invalidated by the next routine inventory report.
    /// A row with no signature yet (brand new, or never reviewed) inherits one automatically the
    /// moment some other row's identical script content has already been signed (see
    /// HomebrewUpgradeScript.Build) — a human still has to review and sign the very first Homebrew
    /// script, but every other application sharing that exact content never needs its own separate
    /// review.
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

            // Retires a row this same method used to write directly under the host's real OS
            // platform, as UpgradeMethod.PackageManagerCommand, before Homebrew moved to this fixed
            // Generic/Script shape — left in place, it would keep winning GetSummariesAsync's
            // per-host platform lookup (checked before its Generic fallback) and permanently shadow
            // the row this method writes now, so the "not installed"/empty-script symptoms would
            // survive this fix for any application that already has one.
            var legacyRow = (await _upgradePathRepository.GetAllForApplicationAsync(entry.Name, cancellationToken))
                .FirstOrDefault(p => p.Platform != PlatformBucket.Generic && p.Method == UpgradeMethod.PackageManagerCommand);
            if (legacyRow is not null)
            {
                _upgradePathRepository.Remove(legacyRow);
            }

            var script = HomebrewUpgradeScript.Build(isSelfUpdate: false);
            var existing = await _upgradePathRepository.GetAsync(entry.Name, PlatformBucket.Generic, cancellationToken);

            if (existing is null)
            {
                var created = UpgradePath.Create(
                    entry.Name, PlatformBucket.Generic, UpgradePathStatus.Found, entry.AvailableVersion,
                    UpgradeMethod.Script, downloadUrl: null, command: null, instructions: null, sourceUrl: null, notes: null,
                    script: script, applicationIdentifier: entry.Name);

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
                    script: script, applicationIdentifier: entry.Name);

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
