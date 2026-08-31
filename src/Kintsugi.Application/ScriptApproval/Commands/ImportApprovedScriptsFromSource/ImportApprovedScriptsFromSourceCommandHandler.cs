using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.ScriptApproval.Commands.ImportApprovedScriptsFromSource;

public class ImportApprovedScriptsFromSourceCommandHandler
    : IRequestHandler<ImportApprovedScriptsFromSourceCommand, ImportApprovedScriptsResultDto>
{
    private readonly IScriptApprovalSourceClient _sourceClient;
    private readonly IScriptSignatureVerifier _signatureVerifier;
    private readonly IApprovedScriptRepository _approvedScripts;
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly IArtifactSigningService _artifactSigningService;
    private readonly IUnitOfWork _unitOfWork;

    public ImportApprovedScriptsFromSourceCommandHandler(
        IScriptApprovalSourceClient sourceClient,
        IScriptSignatureVerifier signatureVerifier,
        IApprovedScriptRepository approvedScripts,
        IUpgradePathRepository upgradePaths,
        IArtifactSigningService artifactSigningService,
        IUnitOfWork unitOfWork)
    {
        _sourceClient = sourceClient;
        _signatureVerifier = signatureVerifier;
        _approvedScripts = approvedScripts;
        _upgradePaths = upgradePaths;
        _artifactSigningService = artifactSigningService;
        _unitOfWork = unitOfWork;
    }

    public async Task<ImportApprovedScriptsResultDto> Handle(
        ImportApprovedScriptsFromSourceCommand request, CancellationToken cancellationToken)
    {
        var status = await _sourceClient.GetStatusAsync(cancellationToken);
        if (status.UnavailableReason is not null || status.HeadCommitSha is null)
        {
            // Thrown rather than reported here, unlike everything below: without a commit there is
            // nothing to import at all, and the page renders this as the refresh having failed.
            throw new InvalidOperationException(
                status.UnavailableReason ?? $"Could not determine the current commit of {status.Repository}.");
        }

        var corpus = await _sourceClient.GetCorpusAsync(status.HeadCommitSha, cancellationToken);
        var rejected = new List<string>(corpus.SkippedReasons);
        var imported = 0;
        var alreadyKnown = 0;

        foreach (var entry in corpus.Entries)
        {
            // Derived from the bucket the entry claims, not read from the entry's own Language field,
            // and then checked against it. ScriptLanguages.For is the single function that decides
            // which interpreter a bucket's scripts run under, so letting an entry assert its own
            // language would let it disagree with the one thing that governs execution.
            var expectedLanguage = ScriptLanguages.For(entry.Metadata.PlatformBucket);
            if (entry.Metadata.Language != expectedLanguage)
            {
                // This is the shape of the bug the shared `generic` bucket used to permit: a Windows
                // host handed a genuinely-signed `#!/bin/bash` script. Refused outright rather than
                // corrected, because whichever of the two fields is wrong, the entry is not
                // describing something safe to run.
                rejected.Add($"{entry.Sha256}: claims {entry.Metadata.Language} but bucket "
                    + $"'{entry.Metadata.PlatformBucket}' runs {expectedLanguage}.");
                continue;
            }

            foreach (var signature in entry.Signatures)
            {
                if (!_signatureVerifier.Verify(entry.Script, signature.Signature, signature.SignerPublicKeyPem))
                {
                    rejected.Add($"{entry.Sha256}: signature from {signature.SignerFingerprint} does not verify.");
                    continue;
                }

                // The fingerprint has to be the one the key actually hashes to, or an entry could
                // claim any provenance it liked — including this server's own — while carrying a
                // different key. That claim is the only thing shown to whoever decides to adopt.
                var actualFingerprint = ScriptSignerFingerprint.For(signature.SignerPublicKeyPem);
                if (!string.Equals(actualFingerprint, signature.SignerFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    rejected.Add($"{entry.Sha256}: signature claims fingerprint {signature.SignerFingerprint} "
                        + $"but its key hashes to {actualFingerprint}.");
                    continue;
                }

                var existing = await _approvedScripts.GetAsync(entry.Sha256, actualFingerprint, cancellationToken);
                if (existing is not null)
                {
                    existing.Refresh(
                        entry.Metadata.ApplicationName, entry.Metadata.ApplicationIdentifier,
                        signature.SignedBy, status.HeadCommitSha);
                    alreadyKnown++;
                    continue;
                }

                await _approvedScripts.AddAsync(
                    ApprovedScript.Create(
                        entry.Sha256,
                        entry.Metadata.PlatformBucket,
                        entry.Script,
                        entry.Metadata.ApplicationName,
                        entry.Metadata.ApplicationIdentifier,
                        actualFingerprint,
                        signature.SignerPublicKeyPem,
                        signature.Signature,
                        signature.SignedBy,
                        signature.SignedAtUtc,
                        status.HeadCommitSha),
                    cancellationToken);
                imported++;
            }
        }

        var blessed = await BlessMatchingLocalRowsAsync(corpus, rejected, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new ImportApprovedScriptsResultDto(
            status.Repository, status.HeadCommitSha, imported, alreadyKnown, blessed, rejected);
    }

    /// <summary>
    /// Signs local rows whose script is already byte-for-byte an approved one.
    /// </summary>
    /// <remarks>
    /// This half is automatic, and safe to be, because no script text changes: the bytes are already
    /// on this server, they were generated here, and all that happens is that a review performed
    /// elsewhere is recognised as covering them. It is the same reasoning as
    /// <c>SignUpgradePathScriptCommandHandler</c>'s sibling-row propagation, extended across servers.
    ///
    /// Adoption — taking on content this server does <em>not</em> have — is deliberately not done
    /// here. It needs a per-row decision by a human, because merging to the approval repository would
    /// otherwise be enough to place new executable content on every server that refreshes. See
    /// <c>AdoptApprovedScriptCommandHandler</c>.
    /// </remarks>
    private async Task<List<BlessedUpgradePathDto>> BlessMatchingLocalRowsAsync(
        ApprovedScriptCorpusReadResult corpus, List<string> rejected, CancellationToken cancellationToken)
    {
        var blessed = new List<BlessedUpgradePathDto>();

        foreach (var entry in corpus.Entries)
        {
            var verifiedSignature = entry.Signatures.FirstOrDefault(
                s => _signatureVerifier.Verify(entry.Script, s.Signature, s.SignerPublicKeyPem));
            if (verifiedSignature is null)
            {
                continue;
            }

            // Matched on content, which is what makes this a bless rather than a replacement — and
            // GetUnsignedRowsWithScriptAsync only ever returns rows with no signature, so a row an
            // agent is already running cannot be caught up in this.
            var candidates = await _upgradePaths.GetUnsignedRowsWithScriptAsync(entry.Script, cancellationToken);

            foreach (var row in candidates)
            {
                // A local row holding these exact bytes under a bucket that runs a different
                // interpreter is a local inconsistency this must not paper over by making it
                // executable. Left unsigned and reported.
                if (ScriptLanguages.For(row.Platform) != entry.Metadata.Language)
                {
                    rejected.Add($"{row.ApplicationName} on {row.Platform}: holds approved {entry.Metadata.Language} "
                        + $"content under a bucket that runs {ScriptLanguages.For(row.Platform)}, so it was left unsigned.");
                    continue;
                }

                // Signed with this server's own key, never with the approving server's: every agent
                // pins one key at enrollment and it is its own server's. See
                // UpgradePath.AdoptApprovedScript for the same point.
                row.SignScript(_artifactSigningService.Sign(row.Script)!);
                blessed.Add(new BlessedUpgradePathDto(row.ApplicationName, row.Platform, verifiedSignature.SignerFingerprint));
            }
        }

        return blessed;
    }
}
