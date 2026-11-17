using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.Baselines.Commands.SignBaselineVersion;

public class SignBaselineVersionCommandHandler : IRequestHandler<SignBaselineVersionCommand, BaselineVersionDto>
{
    private readonly IBaselineRepository _baselines;
    private readonly IArtifactSigningService _signing;
    private readonly IUnitOfWork _unitOfWork;

    public SignBaselineVersionCommandHandler(IBaselineRepository baselines, IArtifactSigningService signing, IUnitOfWork unitOfWork)
    {
        _baselines = baselines;
        _signing = signing;
        _unitOfWork = unitOfWork;
    }

    public async Task<BaselineVersionDto> Handle(SignBaselineVersionCommand request, CancellationToken cancellationToken)
    {
        // No hash, no signature — the fallback to "sign whatever is stored" is the bug this exists to close.
        if (string.IsNullOrWhiteSpace(request.ExpectedDocumentSha256))
        {
            throw new DomainException(
                "A signature must name the document that was reviewed: expectedDocumentSha256 is required. Reload the page and try again.");
        }

        var version = await _baselines.GetVersionAsync(request.VersionId, cancellationToken)
            ?? throw new NotFoundException($"No baseline version with id '{request.VersionId}'.");

        if (!string.Equals(ScriptContentHash.Of(version.Document), request.ExpectedDocumentSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException(
                $"Version {version.Number} is not the document you reviewed. Nothing was signed. Reload the baseline and review it again.");
        }

        // A document this server did not write, or one copied from another version, is refused before the
        // key ever sees it: an agent trusts the ids inside the signed bytes.
        var document = BaselineDocument.Parse(version.Document);
        if (document.VersionId != version.Id || document.BaselineId != version.BaselineId || document.Version != version.Number)
        {
            throw new DomainException($"Version {version.Number}'s document names a different baseline or version. Nothing was signed.");
        }

        version.Sign(_signing.Sign(version.Document)!, request.SignedBy, DateTimeOffset.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new BaselineVersionDto(
            version.Id, version.Number, version.Document, version.DocumentSha256, version.IsSigned,
            version.SignedBy, version.SignedUtc, version.CreatedBy, version.CreatedAtUtc);
    }
}
