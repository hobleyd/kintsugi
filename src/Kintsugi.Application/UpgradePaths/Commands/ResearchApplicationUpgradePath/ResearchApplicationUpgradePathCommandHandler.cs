using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths.Commands.ResearchApplicationUpgradePath;

public class ResearchApplicationUpgradePathCommandHandler : IRequestHandler<ResearchApplicationUpgradePathCommand, ResearchApplicationUpgradePathResult>
{
    // Which package managers are recognized — and the fixed, server-written script each one's
    // applications resolve to instead of an AI call — lives in PackageManagerCatalog, so the
    // registration-time seeding path (RegisterApplicationsCommandHandler) recognizes exactly the
    // same set this scan does.

    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IUpgradePathResearchClient _researchClient;
    private readonly IArtifactSigningService _artifactSigningService;
    private readonly IUnitOfWork _unitOfWork;

    public ResearchApplicationUpgradePathCommandHandler(
        IUpgradePathRepository upgradePathRepository,
        IUpgradePathResearchClient researchClient,
        IArtifactSigningService artifactSigningService,
        IUnitOfWork unitOfWork)
    {
        _upgradePathRepository = upgradePathRepository;
        _researchClient = researchClient;
        _artifactSigningService = artifactSigningService;
        _unitOfWork = unitOfWork;
    }

    public async Task<ResearchApplicationUpgradePathResult> Handle(ResearchApplicationUpgradePathCommand request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Kind is UpgradePathWorkKind.PackageManagerManaged or UpgradePathWorkKind.PackageManagerSelfUpdate)
            {
                await RetireLegacyPackageManagerRowAsync(request.ApplicationName, request.Platform, cancellationToken);
            }

            var existing = await _upgradePathRepository.GetAsync(request.ApplicationName, request.Platform, cancellationToken);

            // Already resolved by an earlier scan. NotFound and Failed rows fall through and are
            // retried, since a new release (or a fixed bug on our end) could change the outcome.
            // Re-checking an already-Found script's version is "Check for Updates"' job now, not a
            // side effect of finding new upgrade paths — this always just skips.
            if (existing is not null && existing.Status == UpgradePathStatus.Found && !request.ForceRecheck)
            {
                // A row resolved before artifact signing existed (or otherwise left unsigned)
                // carries a Command an agent's is_patchable check will always reject. Backfilling
                // here means the routine fleet scan self-heals every such row instead of silently
                // leaving it unpatchable until someone happens to force a per-application refresh.
                if (existing.Command is not null && existing.CommandSignature is null)
                {
                    existing.SetSignatures(existing.ScriptSignature, _artifactSigningService.Sign(existing.Command));
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                }

                // A brand-new (never-reviewed) script normally sits unsigned until a human uses
                // "Sign Script" — but every Homebrew script is now byte-identical to every other
                // Homebrew script sharing the same isSelfUpdate case (see HomebrewUpgradeScript.Build),
                // so once a human has signed this exact content anywhere, this row can safely inherit
                // that same trust instead of sitting unsigned indefinitely.
                if (existing.Method == UpgradeMethod.Script && existing.Script is not null && existing.ScriptSignature is null)
                {
                    var inheritedSignature = await _upgradePathRepository.FindExistingSignatureForScriptAsync(existing.Script, cancellationToken);
                    if (inheritedSignature is not null)
                    {
                        existing.SetSignatures(inheritedSignature, existing.CommandSignature);
                        await _unitOfWork.SaveChangesAsync(cancellationToken);
                    }
                }

                return ToResult(existing, Skipped: true, note: null);
            }

            return request.Kind switch
            {
                UpgradePathWorkKind.PackageManagerManaged => await ApplyPackageManagerCommandAsync(
                    request, request.PackageManagerName!, isSelfUpdate: false, cancellationToken),
                UpgradePathWorkKind.PackageManagerSelfUpdate => await ApplyPackageManagerCommandAsync(
                    request, request.PackageManagerName!, isSelfUpdate: true, cancellationToken),
                _ => await GenerateScriptViaAiAsync(request, cancellationToken)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything unexpected here (e.g. the database itself is unreachable) is still a
            // failure worth surfacing rather than losing — best-effort persist, but report the
            // failure regardless of whether that persist attempt itself succeeds.
            var note = $"Unexpected error: {ex.Message}";
            try
            {
                var entity = await UpsertAsync(request.ApplicationName, request.Platform, UpgradePathStatus.Failed, null, UpgradeMethod.Unknown, null, null, null, null, note, null, request.ApplicationIdentifier, cancellationToken);
                return ToResult(entity, Skipped: false, note);
            }
            catch (Exception)
            {
                // Swallowed: we already have a result to report even if this persist attempt failed too.
            }

            return new ResearchApplicationUpgradePathResult(request.ApplicationName, request.Platform, UpgradePathStatus.Failed, Skipped: false, note);
        }
    }

    /// <summary>
    /// Removes rows this application may still carry from either of two superseded shapes, both of
    /// which would keep winning <c>GetSummariesAsync</c>'s per-host platform lookup (tried before
    /// its package-manager fallback) and permanently shadow the row this handler resolves to below:
    /// <list type="bullet">
    /// <item>a row stored under the host's real OS platform as
    /// <see cref="UpgradeMethod.PackageManagerCommand"/>, from before Homebrew moved to a fixed
    /// Script shape (the same legacy shape <c>RegisterApplicationsCommandHandler</c>'s own copy of
    /// this cleanup retires); and</item>
    /// <item>a row under the old shared <see cref="PlatformBucket.Generic"/> bucket, from before
    /// package-manager rows moved to their own per-manager bucket. The
    /// <c>SplitPackageManagerPlatformBucket</c> migration rewrites the ones that existed at deploy
    /// time, so this only catches a row some older node wrote in between.</item>
    /// </list>
    /// This must run unconditionally rather than only when nothing's resolved yet, since the
    /// "already Found, skip" branch right after it would otherwise leave the shadowing row in place
    /// forever. Saves immediately: that branch doesn't always call
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> itself, and a Remove nobody flushes is a no-op.
    /// </summary>
    private async Task RetireLegacyPackageManagerRowAsync(string applicationName, string currentPlatform, CancellationToken cancellationToken)
    {
        var legacyRows = (await _upgradePathRepository.GetAllForApplicationAsync(applicationName, cancellationToken))
            .Where(p => p.Platform != currentPlatform
                && ((p.Platform != PlatformBucket.Generic && p.Method == UpgradeMethod.PackageManagerCommand)
                    || p.Platform == PlatformBucket.Generic))
            .ToList();

        if (legacyRows.Count == 0)
        {
            return;
        }

        foreach (var row in legacyRows)
        {
            _upgradePathRepository.Remove(row);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<ResearchApplicationUpgradePathResult> ApplyPackageManagerCommandAsync(
        ResearchApplicationUpgradePathCommand request, string managerName, bool isSelfUpdate, CancellationToken cancellationToken)
    {
        if (!PackageManagerCatalog.TryGet(managerName, out var packageManager))
        {
            var unrecognizedNote = isSelfUpdate
                ? $"{request.ApplicationName}: a new package manager type — no default self-update command is known for it."
                : $"{request.ApplicationName}: managed by '{managerName}', which isn't a recognized package manager yet — no default upgrade command is known for it.";

            var unrecognizedEntity = await UpsertAsync(
                request.ApplicationName, request.Platform, UpgradePathStatus.NotFound, null, UpgradeMethod.Unknown,
                null, null, null, null, unrecognizedNote, null, request.ApplicationIdentifier, cancellationToken);
            return ToResult(unrecognizedEntity, Skipped: false, unrecognizedNote);
        }

        // A deterministic, server-written script — not AI-generated — implementing the same
        // --appName/--appId/--update-version/--update CLI contract a Research-kind script does, so
        // a package-manager-managed application gets a real, checkable, signable upgrade path too
        // instead of a bare command with no way to learn the latest version short of asking the
        // managed host. bash for Homebrew, PowerShell for winget/Chocolatey — see
        // PackageManagerCatalog.
        var script = packageManager.BuildScript(isSelfUpdate);

        // What the script is addressed by. Homebrew has no real bundle identifier for a
        // formula/cask (see InstalledApplication.ApplicationIdentifier), so the package name stands
        // in; winget and Chocolatey both genuinely address a package by its id, which the scan
        // planner carries through on the work item. Either way the script only ever reads it from
        // --appId at runtime and never bakes it into its own text (see the *UpgradeScript builders)
        // — that identical content across every application is what lets one signed script cover
        // them all.
        var applicationIdentifier = request.ApplicationIdentifier ?? request.ApplicationName;

        // Runs right here on the server (bash or pwsh, per the script's own platform bucket) against
        // the manager's public catalog — never on the managed host — so the Applications page shows
        // a real latest version immediately, the same way a freshly generated Research-kind script's
        // version gets checked once it exists.
        var latestVersion = await _researchClient.CheckScriptVersionAsync(
            script, request.Platform, request.ApplicationName, applicationIdentifier, cancellationToken);

        // Every script from one manager (per isSelfUpdate) is byte-identical across every
        // application, so once a human has signed this exact content anywhere, a freshly
        // (re-)resolved row inherits that same trust immediately rather than sitting unsigned until
        // someone happens to sign this particular application too.
        var scriptSignature = await _upgradePathRepository.FindExistingSignatureForScriptAsync(script, cancellationToken);

        var entity = await UpsertAsync(
            request.ApplicationName, request.Platform, UpgradePathStatus.Found, latestVersion, UpgradeMethod.Script,
            null, null, null, null, null, script, applicationIdentifier, cancellationToken, scriptSignature);

        return ToResult(entity, Skipped: false, note: null);
    }

    /// <summary>
    /// The single AI call per application — asks the AI to research and produce the durable
    /// upgrade script in one step (see <see cref="AiUpgradePathResearchClient"/>), then, once a
    /// script exists, runs its own `--update-version` mode right here on the server to populate
    /// <see cref="UpgradePath.LatestVersion"/> — no second AI call, ever, just to learn a version
    /// number.
    /// </summary>
    private async Task<ResearchApplicationUpgradePathResult> GenerateScriptViaAiAsync(ResearchApplicationUpgradePathCommand request, CancellationToken cancellationToken)
    {
        // macOS gets a bash script, Windows a PowerShell one (see BuildScriptGenerationPrompt);
        // every other bucket — Linux, or an OS this system doesn't recognize at all — has no
        // prompt written for it and no way to validate or run what came back, so it resolves to
        // NotFound with a note rather than producing a script nothing can check.
        if (request.Platform != PlatformBucket.MacOs && request.Platform != PlatformBucket.Windows)
        {
            var unsupportedNote = $"{request.ApplicationName}: AI-generated upgrade scripts are currently only supported on macOS and Windows.";
            var unsupportedEntity = await UpsertAsync(request.ApplicationName, request.Platform, UpgradePathStatus.NotFound, null, UpgradeMethod.Unknown, null, null, null, null, unsupportedNote, null, request.ApplicationIdentifier, cancellationToken);
            return ToResult(unsupportedEntity, Skipped: false, unsupportedNote);
        }

        if (request.Settings is null)
        {
            var unconfiguredNote = $"{request.ApplicationName}: the AI agent is not configured or not enabled — configure it under Settings first.";
            var unconfiguredEntity = await UpsertAsync(request.ApplicationName, request.Platform, UpgradePathStatus.NotFound, null, UpgradeMethod.Unknown, null, null, null, null, unconfiguredNote, null, request.ApplicationIdentifier, cancellationToken);
            return ToResult(unconfiguredEntity, Skipped: false, unconfiguredNote);
        }

        UpgradePathScriptResult result;
        try
        {
            result = await _researchClient.GenerateScriptAsync(
                request.Settings,
                new UpgradePathScriptGenerationRequest(request.ApplicationName, request.Platform, request.KnownVersions, request.ApplicationIdentifier, request.PromptOverride),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var note = $"Could not generate an upgrade script — {ex.Message}";
            var failedEntity = await UpsertAsync(request.ApplicationName, request.Platform, UpgradePathStatus.Failed, null, UpgradeMethod.Unknown, null, null, null, null, note, null, request.ApplicationIdentifier, cancellationToken);
            return ToResult(failedEntity, Skipped: false, note);
        }

        // The version shown in the table comes from actually running the script's own
        // --update-version mode, not from anything the AI said — that's the whole point of the
        // redesign. Best-effort: a failure here just leaves LatestVersion unset for now, rather
        // than demoting an otherwise-successfully-generated script to Failed.
        string? latestVersion = null;
        if (result.Status == UpgradePathStatus.Found && result.Script is not null && !string.IsNullOrWhiteSpace(request.ApplicationIdentifier))
        {
            latestVersion = await _researchClient.CheckScriptVersionAsync(result.Script, request.Platform, request.ApplicationName, request.ApplicationIdentifier, cancellationToken);
        }

        var entity = await UpsertAsync(
            request.ApplicationName, request.Platform, result.Status, latestVersion,
            result.Status == UpgradePathStatus.Found ? UpgradeMethod.Script : UpgradeMethod.Unknown,
            null, null, null, null, result.Notes, result.Script, request.ApplicationIdentifier, cancellationToken);

        return ToResult(entity, Skipped: false, result.Notes);
    }

    private async Task<UpgradePath> UpsertAsync(
        string applicationName, string platform, UpgradePathStatus status, string? latestVersion, UpgradeMethod method,
        string? downloadUrl, string? command, string? instructions, string? sourceUrl, string? notes, string? script,
        string? applicationIdentifier, CancellationToken cancellationToken, string? scriptSignature = null)
    {
        var existing = await _upgradePathRepository.GetAsync(applicationName, platform, cancellationToken);

        UpgradePath entity;
        if (existing is null)
        {
            entity = UpgradePath.Create(applicationName, platform, status, latestVersion, method, downloadUrl, command, instructions, sourceUrl, notes, script, applicationIdentifier);
            await _upgradePathRepository.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.Update(status, latestVersion, method, downloadUrl, command, instructions, sourceUrl, notes, script, applicationIdentifier);
            entity = existing;
        }

        // Command is signed over whatever ended up on the entity, right before the save that makes
        // it reachable by an agent at all — see SaveUpgradePathCommandHandler for the same pattern.
        // Script is left as whatever the caller already knows to be true for this exact content —
        // null for a freshly AI-generated script, which always needs its own human review via the
        // "Sign Script" action, or an already-signed content match a caller (e.g.
        // ApplyPackageManagerCommandAsync) looked up itself for a script that's provably identical
        // to one a human already reviewed.
        entity.SetSignatures(scriptSignature, _artifactSigningService.Sign(entity.Command));

        // Saved immediately, one application at a time, rather than batched at the end of a scan —
        // so results become visible (via GET /api/upgrade-paths and its summary) as they come in,
        // not only once the whole scan (which can cover hundreds of applications) finishes.
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return entity;
    }

    private static ResearchApplicationUpgradePathResult ToResult(UpgradePath entity, bool Skipped, string? note) =>
        new(entity.ApplicationName, entity.Platform, entity.Status, Skipped, note,
            entity.LatestVersion, entity.Method, entity.DownloadUrl, entity.Command, entity.Instructions, entity.SourceUrl, entity.CheckedUtc, entity.Script);
}
