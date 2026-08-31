using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.ScriptApproval.Commands.AdoptApprovedScript;

public class AdoptApprovedScriptCommandHandler : IRequestHandler<AdoptApprovedScriptCommand, AdoptApprovedScriptResultDto>
{
    private readonly IApprovedScriptRepository _approvedScripts;
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly IScriptSignatureVerifier _signatureVerifier;
    private readonly IArtifactSigningService _artifactSigningService;
    private readonly IUnitOfWork _unitOfWork;

    public AdoptApprovedScriptCommandHandler(
        IApprovedScriptRepository approvedScripts,
        IUpgradePathRepository upgradePaths,
        IScriptSignatureVerifier signatureVerifier,
        IArtifactSigningService artifactSigningService,
        IUnitOfWork unitOfWork)
    {
        _approvedScripts = approvedScripts;
        _upgradePaths = upgradePaths;
        _signatureVerifier = signatureVerifier;
        _artifactSigningService = artifactSigningService;
        _unitOfWork = unitOfWork;
    }

    public async Task<AdoptApprovedScriptResultDto> Handle(
        AdoptApprovedScriptCommand request, CancellationToken cancellationToken)
    {
        var approved = await _approvedScripts.GetAsync(request.Sha256, request.SignerFingerprint, cancellationToken)
            ?? throw new NotFoundException(
                $"No approved script {request.Sha256} signed by {request.SignerFingerprint} has been imported.");

        var path = await _upgradePaths.GetAsync(request.ApplicationName, request.Platform, cancellationToken)
            ?? throw new NotFoundException(
                $"No upgrade path found for '{request.ApplicationName}' on '{request.Platform}'.");

        // Re-checked here rather than trusted from import time. The stored row is the one about to be
        // executed, and a signature that verified when it was read is the only evidence that its
        // script text hasn't been altered in this database since — which is exactly the case where
        // adopting would be handing agents something nobody reviewed.
        if (!_signatureVerifier.Verify(approved.Script, approved.Signature, approved.SignerPublicKeyPem))
        {
            throw new DomainException(
                $"The stored signature for {request.Sha256} no longer verifies against its own key, so its script "
                + "cannot be shown to be the reviewed content. Refresh scripts and try again.");
        }

        // Both buckets go through the same function, so this catches an approved macOS script being
        // put on a Windows row even when both sides individually look consistent. It is the check
        // that stops a genuinely-signed `#!/bin/bash` script reaching a PowerShell host.
        var approvedLanguage = ScriptLanguages.For(approved.PlatformBucket);
        var targetLanguage = ScriptLanguages.For(path.Platform);
        if (approvedLanguage != targetLanguage)
        {
            throw new DomainException(
                $"That script was approved for '{approved.PlatformBucket}', which runs {approvedLanguage}, but "
                + $"'{path.Platform}' runs {targetLanguage}.");
        }

        // Signed with this server's key — see UpgradePath.AdoptApprovedScript, which also refuses if
        // the row already carries a signature.
        path.AdoptApprovedScript(
            approved.Script,
            _artifactSigningService.Sign(approved.Script)!,
            approved.ApplicationIdentifier);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new AdoptApprovedScriptResultDto(
            path.ApplicationName, path.Platform, approved.Sha256, approved.SignerFingerprint);
    }
}
