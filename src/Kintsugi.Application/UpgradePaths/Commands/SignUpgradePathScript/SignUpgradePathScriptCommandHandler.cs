using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.UpgradePaths.Commands.SignUpgradePathScript;

public class SignUpgradePathScriptCommandHandler : IRequestHandler<SignUpgradePathScriptCommand, UpgradePathResultDto>
{
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IArtifactSigningService _artifactSigningService;
    private readonly IUpgradePathResearchClient _researchClient;
    private readonly IScriptApprovalPublisher _approvalPublisher;
    private readonly IUnitOfWork _unitOfWork;

    public SignUpgradePathScriptCommandHandler(
        IUpgradePathRepository upgradePathRepository, IArtifactSigningService artifactSigningService,
        IUpgradePathResearchClient researchClient, IScriptApprovalPublisher approvalPublisher, IUnitOfWork unitOfWork)
    {
        _upgradePathRepository = upgradePathRepository;
        _artifactSigningService = artifactSigningService;
        _researchClient = researchClient;
        _approvalPublisher = approvalPublisher;
        _unitOfWork = unitOfWork;
    }

    public async Task<UpgradePathResultDto> Handle(SignUpgradePathScriptCommand request, CancellationToken cancellationToken)
    {
        var existing = await _upgradePathRepository.GetAsync(request.ApplicationName, request.Platform, cancellationToken)
            ?? throw new NotFoundException($"No upgrade path found for '{request.ApplicationName}' on '{request.Platform}'.");

        if (existing.Script is null)
        {
            throw new DomainException($"'{request.ApplicationName}' on '{request.Platform}' has no script to sign.");
        }

        // Signed here, right before the save that makes this signature reachable by an agent at
        // all, over exactly what's already persisted — never over unsaved editor content — so a
        // signature only ever vouches for what a later read (and so a later execution) will see.
        var signature = _artifactSigningService.Sign(existing.Script)!;
        existing.SignScript(signature);

        // Every Homebrew script (per isSelfUpdate case) is now byte-for-byte identical across every
        // application (see HomebrewUpgradeScript.Build) — a human reviewing and signing it here is
        // vouching for that exact content, not for this one application specifically, so every other
        // already-resolved row sharing it becomes trusted immediately too, rather than only
        // self-healing the next time each one happens to get rescanned or re-registered.
        var siblingRows = await _upgradePathRepository.GetUnsignedRowsWithScriptAsync(existing.Script, cancellationToken);
        foreach (var sibling in siblingRows.Where(row => row.Id != existing.Id))
        {
            sibling.SignScript(signature);
        }

        // Sign is also the first moment the Applications page's Status/Latest columns can stop
        // showing "Review And Sign" — so run the freshly-signed script's own --update-version mode
        // right here, the same way "Check for Updates" does, instead of leaving LatestVersion at
        // whatever (possibly nothing) it was before review. Best-effort: CheckScriptVersionAsync
        // returns null on any failure, which just leaves LatestVersion unset for now.
        if (!string.IsNullOrWhiteSpace(existing.ApplicationIdentifier))
        {
            var latestVersion = await _researchClient.CheckScriptVersionAsync(
                existing.Script, existing.Platform, existing.ApplicationName, existing.ApplicationIdentifier, cancellationToken);
            existing.UpdateDiscoveredLatestVersion(latestVersion);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Published *after* the save, deliberately. The approval repository is a record of decisions
        // this server has already made, so the local signature must be durable before anything is
        // proposed — the reverse order could open a pull request for an approval that a failed save
        // then threw away, and a merged record of an approval nobody made is worse than a missing one.
        // PublishAsync reports its own failures rather than throwing, so nothing here can undo the
        // signing that already succeeded.
        var approval = await _approvalPublisher.PublishAsync(
            new ScriptApprovalSubmission(
                ScriptContentHash.Of(existing.Script),
                existing.Platform,
                ScriptLanguages.For(existing.Platform),
                existing.Script,
                existing.ApplicationName,
                existing.ApplicationIdentifier,
                _artifactSigningService.GetPublicKeyFingerprint(),
                _artifactSigningService.GetPublicKeyPem(),
                signature,
                request.SignedBy,
                DateTimeOffset.UtcNow),
            cancellationToken);

        return new UpgradePathResultDto(
            existing.ApplicationName, existing.Platform, existing.Status, existing.LatestVersion, existing.Method,
            existing.DownloadUrl, existing.Command, existing.Instructions, existing.SourceUrl, existing.Notes, existing.CheckedUtc, existing.Script,
            existing.ScriptSignature is not null,
            approval.Outcome,
            approval.PullRequestUrl,
            approval.Message);
    }
}
