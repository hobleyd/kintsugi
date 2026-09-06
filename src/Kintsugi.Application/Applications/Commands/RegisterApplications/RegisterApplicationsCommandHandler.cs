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

            var entity = new InstalledApplication(host.Id, entry.Name, entry.Version, entry.ApplicationIdentifier, entry.UpdateAvailable);
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
    /// to (re-)discover the same information less reliably. An entry whose manager reports a
    /// pending update <em>without</em> a version (<see cref="ApplicationEntry.UpdateAvailable"/>
    /// true, <see cref="ApplicationEntry.AvailableVersion"/> null — Flatpak on a host whose
    /// appstream cache has nothing to say) seeds the row too, since the row is what makes the
    /// installation patchable at all: the agent's <c>is_patchable</c> runs nothing but a signed
    /// Script row, and the Hosts screen counts nothing that has no row to resolve to. Such a row
    /// keeps whatever <see cref="UpgradePath.LatestVersion"/> it already had rather than having it
    /// erased by a report that simply did not know one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manager's <em>own</em> row ("Homebrew", "winget", ...) is seeded here as well, whether or
    /// not it reports a version — every agent reports its manager with <c>available_version</c>
    /// unset, since no manager lists itself in its own outdated report. It is the same script as
    /// every application under it (see <c>RecognizedPackageManager.BuildScript</c>), and the
    /// Applications screen nests those applications under the manager's row and shows the script
    /// there once on behalf of all of them — so a manager with no row reads as "not checked yet"
    /// above a list of applications that each plainly have one. Before this the manager's row
    /// existed only once somebody pressed "Find Upgrade Paths". Its <see cref="UpgradePath.LatestVersion"/>
    /// stays whatever it was — the update-check coordinator fills it from the script's own
    /// <c>--update-version</c>; an agent check-in is no place for a call to GitHub.
    /// </para>
    /// <para>
    /// Stored under the same
    /// <see cref="PlatformBucket.ForPackageManager"/> bucket and <see cref="UpgradeMethod.Script"/>
    /// shape <c>ResearchApplicationUpgradePathCommandHandler</c> uses (never the real per-host OS
    /// platform, and never <see cref="UpgradeMethod.PackageManagerCommand"/>) — otherwise this row
    /// would never match the key the scan planner and the Applications page's per-row panel both
    /// look it up by, and an agent would never recognize it as patchable.
    /// An entry naming a manager this system doesn't recognize is left entirely alone here: there
    /// is no script to write for it, and the scan is what resolves it to NotFound with a note.
    /// A row with no signature yet (brand new, or never reviewed) takes whatever the rest of its
    /// bucket already runs — the bucket's signed script and its signature, when there is one — so a
    /// human still has to review and sign the very first script per manager, but every application
    /// that turns up afterwards joins that reviewed script at once. See <see cref="PackageManagerBucketScript"/>.
    /// </para>
    /// <para>
    /// A row that already carries a <see cref="UpgradePath.ScriptSignature"/> keeps its script
    /// exactly as reviewed; only its <see cref="UpgradePath.LatestVersion"/> moves. This used to
    /// rewrite <see cref="UpgradePath.Script"/> from the builder unconditionally, under the belief
    /// that "the script content for a given manager never changes". It does
    /// change — every time one of the <c>*UpgradeScript.Build</c> bodies is edited — so what that
    /// actually meant was that a routine inventory report could swap the content of a signed row,
    /// content the fleet's agents may be executing right now, on the strength of a deployment
    /// nobody was watching. It is the same thing <see cref="UpgradePath.AdoptApprovedScript"/>
    /// refuses to do, for the same reason, and a background report has less business doing it than
    /// a human pressing Adopt does.
    ///
    /// So a signed script survives a server upgrade, and taking a newer server-written one is a
    /// deliberate act: the Upgrade Scripts page shows which scripts this build would now write
    /// differently (<see cref="PackageManagerCatalog.CurrentScriptFor"/>) and
    /// <c>TakeServerWrittenScriptCommand</c> replaces one — on every row holding it — unsigned, for
    /// review. Until somebody does, a row seeded after the deployment gets the <em>reviewed</em> text
    /// rather than the builder's, or the bucket would split in two and the new rows would sit
    /// unsigned and unpatchable — which is exactly what happened.
    /// </para>
    /// </remarks>
    private async Task UpsertPackageManagerUpgradePathsAsync(IReadOnlyList<ApplicationEntry> applications, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // One lookup per manager per report, not per application: a fresh host reports a hundred
        // Homebrew rows and every one of them gets the same answer. Nothing written in this loop is
        // saved before the report finishes, so the answer cannot change under it either.
        var bucketScripts = new Dictionary<string, PackageManagerBucketScript>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in applications)
        {
            RecognizedPackageManager packageManager;
            if (!string.IsNullOrEmpty(entry.PackageManager))
            {
                var reportsUpdate = !string.IsNullOrEmpty(entry.AvailableVersion) || entry.UpdateAvailable == true;
                if (!reportsUpdate || !PackageManagerCatalog.TryGet(entry.PackageManager, out packageManager))
                {
                    continue;
                }
            }
            else if (!PackageManagerCatalog.TryGet(entry.Name, out packageManager))
            {
                // Neither managed nor a manager: an application the scan researches, not this.
                continue;
            }

            if (!seen.Add(entry.Name))
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

            // winget and Chocolatey address a package by its id; Homebrew has none, so the package
            // name stands in. Either way this is only ever handed straight back to the script as
            // --appId — see the *UpgradeScript builders.
            var applicationIdentifier = entry.ApplicationIdentifier ?? entry.Name;
            var existing = await _upgradePathRepository.GetAsync(entry.Name, platform, cancellationToken);

            // A reviewed script is left exactly as it was reviewed — see the remarks above. A row
            // nobody has signed yet, and a row that does not exist, gets what the rest of its bucket
            // already runs: the bucket's signed script and signature when there is one, the builder's
            // text only when nothing in the bucket has been reviewed. Asking the builder first is what
            // used to split a bucket after a deployment — see PackageManagerBucketScript.
            if (existing?.ScriptSignature is not null)
            {
                existing.Update(
                    UpgradePathStatus.Found, entry.AvailableVersion ?? existing.LatestVersion, UpgradeMethod.Script,
                    downloadUrl: null, command: null, instructions: null, sourceUrl: null, notes: null,
                    script: existing.Script, applicationIdentifier: applicationIdentifier);
                continue;
            }

            if (!bucketScripts.TryGetValue(packageManager.Name, out var bucketScript))
            {
                bucketScript = await PackageManagerBucketScript.ResolveAsync(_upgradePathRepository, packageManager, cancellationToken);
                bucketScripts[packageManager.Name] = bucketScript;
            }

            if (existing is null)
            {
                var created = UpgradePath.Create(
                    entry.Name, platform, UpgradePathStatus.Found, entry.AvailableVersion,
                    UpgradeMethod.Script, downloadUrl: null, command: null, instructions: null, sourceUrl: null, notes: null,
                    script: bucketScript.Script, applicationIdentifier: applicationIdentifier);
                created.SetSignatures(bucketScript.Signature, null);
                await _upgradePathRepository.AddAsync(created, cancellationToken);
            }
            else
            {
                existing.Update(
                    UpgradePathStatus.Found, entry.AvailableVersion ?? existing.LatestVersion, UpgradeMethod.Script,
                    downloadUrl: null, command: null, instructions: null, sourceUrl: null, notes: null,
                    script: bucketScript.Script, applicationIdentifier: applicationIdentifier);
                existing.SetSignatures(bucketScript.Signature, existing.CommandSignature);
            }
        }
    }
}
