using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths.Commands.SaveUpgradePath;

public class SaveUpgradePathCommandHandler : IRequestHandler<SaveUpgradePathCommand, UpgradePathResultDto>
{
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IArtifactSigningService _artifactSigningService;
    private readonly IUnitOfWork _unitOfWork;

    public SaveUpgradePathCommandHandler(
        IUpgradePathRepository upgradePathRepository, IArtifactSigningService artifactSigningService, IUnitOfWork unitOfWork)
    {
        _upgradePathRepository = upgradePathRepository;
        _artifactSigningService = artifactSigningService;
        _unitOfWork = unitOfWork;
    }

    public async Task<UpgradePathResultDto> Handle(SaveUpgradePathCommand request, CancellationToken cancellationToken)
    {
        var existing = await _upgradePathRepository.GetAsync(request.ApplicationName, request.Platform, cancellationToken);

        // The AI response schema (and so the pasted-in JSON) has no "status" field — derive it
        // from the method, same as the automated research flow does.
        var status = request.Method == UpgradeMethod.Unknown ? UpgradePathStatus.NotFound : UpgradePathStatus.Found;

        // A hand-saved script row needs an ApplicationIdentifier for exactly the same reason an
        // AI-researched one does: CheckApplicationUpdateCommandHandler refuses to run
        // --update-version without one, so falling back to the application name (rather than
        // leaving it null) is what lets "Find updates" ever pick this row up.
        var applicationIdentifier = request.ApplicationIdentifier ?? request.ApplicationName;

        UpgradePath entity;
        if (existing is null)
        {
            entity = UpgradePath.Create(
                request.ApplicationName, request.Platform, status, request.LatestVersion, request.Method,
                request.DownloadUrl, request.Command, request.Instructions, request.SourceUrl, request.Notes, request.Script,
                applicationIdentifier);
            await _upgradePathRepository.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.Update(
                status, request.LatestVersion, request.Method,
                request.DownloadUrl, request.Command, request.Instructions, request.SourceUrl, request.Notes, request.Script,
                applicationIdentifier);
            entity = existing;
        }

        // Command is signed here, right before the save that makes this content reachable by an
        // agent at all, over whatever ended up on the entity — not the raw request fields — so the
        // signature always matches exactly what a later read (and so a later execution) will see.
        // Script signing is deliberately left out: it now requires a human to review the result
        // and explicitly sign it via the "Sign Script" action (see
        // SignUpgradePathScriptCommandHandler), so a fresh or hand-pasted script always starts out
        // unsigned here, whatever ScriptSignature it may have carried before.
        entity.SetSignatures(null, _artifactSigningService.Sign(entity.Command));

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new UpgradePathResultDto(
            entity.ApplicationName, entity.Platform, entity.Status, entity.LatestVersion, entity.Method,
            entity.DownloadUrl, entity.Command, entity.Instructions, entity.SourceUrl, entity.Notes, entity.CheckedUtc, entity.Script,
            entity.ScriptSignature is not null);
    }
}
