using System.Security.Cryptography;
using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.AgentPackages.Commands.PublishAgentPackage;

public class PublishAgentPackageCommandHandler : IRequestHandler<PublishAgentPackageCommand, AgentPackageDto>
{
    private readonly IAgentPackageRepository _repository;
    private readonly IAgentPackageStorage _storage;
    private readonly IArtifactSigningService _signingService;
    private readonly IUnitOfWork _unitOfWork;

    public PublishAgentPackageCommandHandler(
        IAgentPackageRepository repository,
        IAgentPackageStorage storage,
        IArtifactSigningService signingService,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _storage = storage;
        _signingService = signingService;
        _unitOfWork = unitOfWork;
    }

    public async Task<AgentPackageDto> Handle(PublishAgentPackageCommand request, CancellationToken cancellationToken)
    {
        var platform = request.Platform.Trim().ToLowerInvariant();
        var version = request.Version.Trim();

        var existing = await _repository.GetByPlatformAndVersionAsync(platform, version, cancellationToken);
        if (existing is not null)
        {
            // A release pipeline (see clients/macos-agent/packaging/publish-release.sh, and its
            // GitHub Actions caller) needs to be able to call this on every build without first
            // checking whether this exact version already went out — e.g. a re-run of the same
            // CI job. Re-publishing identical bytes under an already-published (platform, version)
            // is therefore a no-op, not an error. Re-publishing *different* bytes under a version
            // that's already out is still rejected — see AgentPackage's "never overwritten" note —
            // since that almost always means the version number was left unbumped by mistake.
            var incomingSha256Hex = await ComputeSha256Async(request.Content, cancellationToken);
            if (!incomingSha256Hex.Equals(existing.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException(
                    $"Version '{version}' has already been published for platform '{platform}' with different content.");
            }

            return AgentPackageDto.FromEntity(existing);
        }

        var (fileSizeBytes, sha256Hex) = await _storage.SaveAsync(platform, request.FileName, request.Content, cancellationToken);

        // Sign() only ever returns null for a null/empty input, and sha256Hex — just computed from
        // the file that was just written — is never empty, so this is here purely to satisfy the
        // nullable return type, not because it's expected to actually happen.
        var signature = _signingService.Sign(sha256Hex)
            ?? throw new InvalidOperationException("Failed to sign the published package's checksum.");

        var package = AgentPackage.Create(platform, version, request.FileName, fileSizeBytes, sha256Hex, signature, request.ReleaseNotes);

        await _repository.AddAsync(package, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return AgentPackageDto.FromEntity(package);
    }

    /// <summary>Hashes the incoming stream without persisting it anywhere — used only to check
    /// whether a re-publish attempt for an already-published version matches what's already on
    /// disk, so that check never touches (and can't corrupt) the existing stored file.</summary>
    private static async Task<string> ComputeSha256Async(Stream content, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        await using (var hashingStream = new CryptoStream(Stream.Null, sha256, CryptoStreamMode.Write))
        {
            await content.CopyToAsync(hashingStream, cancellationToken);
        }

        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }
}
