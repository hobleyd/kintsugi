using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One version of a <see cref="Baseline"/>: a canonical JSON document listing every item, and —
/// once a person has reviewed it — the server's signature over exactly those bytes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The signature covers <see cref="Document"/> and nothing else</strong>, the same
/// artifact-signing key and ECDSA-SHA256 DER format as an upgrade script (see
/// <c>ArtifactSigningService</c>), so each agent verifies it with the key it pinned at enrollment
/// (<c>identity::verify_artifact_signature</c>) before reading a single item. The document carries
/// its own baseline id, version id and number, so an agent reports results under ids taken from the
/// signed bytes rather than from the unsigned envelope around them.
/// </para>
/// <para>
/// <strong>Immutable once created.</strong> Changing one value makes a new version; a version is
/// signed once and never re-signed or edited, so a signature always vouches for the bytes a reviewer
/// was shown (see <c>SignBaselineVersionCommandHandler</c>).
/// </para>
/// </remarks>
public class BaselineVersion : BaseEntity
{
    public Guid BaselineId { get; private set; }

    public int Number { get; private set; }

    /// <summary>The canonical JSON the signature is over — see <c>BaselineDocument</c>.</summary>
    public string Document { get; private set; } = default!;

    /// <summary>Lowercase hex SHA-256 of <see cref="Document"/>'s UTF-8 bytes (<c>ScriptContentHash.Of</c>).</summary>
    public string DocumentSha256 { get; private set; } = default!;

    public string? CreatedBy { get; private set; }

    public string? Signature { get; private set; }

    public string? SignedBy { get; private set; }

    public DateTimeOffset? SignedUtc { get; private set; }

    public bool IsSigned => Signature is not null;

    private BaselineVersion()
    {
    }

    /// <summary>A new, unsigned version. <paramref name="id"/> is chosen by the caller because the
    /// document names its own version id, so the id has to exist before the document can be built.</summary>
    public static BaselineVersion Create(Guid id, Guid baselineId, int number, string document, string documentSha256, string? createdBy)
    {
        if (id == Guid.Empty || baselineId == Guid.Empty)
        {
            throw new DomainException("A baseline version must have an id and name the baseline it belongs to.");
        }

        if (number < 1)
        {
            throw new DomainException("Baseline versions are numbered from 1.");
        }

        if (string.IsNullOrWhiteSpace(document) || string.IsNullOrWhiteSpace(documentSha256))
        {
            throw new DomainException("A baseline version needs a document and its hash.");
        }

        return new BaselineVersion
        {
            Id = id,
            BaselineId = baselineId,
            Number = number,
            Document = document,
            DocumentSha256 = documentSha256,
            CreatedBy = createdBy
        };
    }

    public void Sign(string signature, string? signedBy, DateTimeOffset signedUtc)
    {
        if (IsSigned)
        {
            throw new DomainException($"Version {Number} is already signed. Changing a baseline makes a new version.");
        }

        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new DomainException("A signature cannot be empty.");
        }

        Signature = signature;
        SignedBy = signedBy;
        SignedUtc = signedUtc;
        MarkUpdated();
    }
}
