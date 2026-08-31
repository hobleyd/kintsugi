using System.Security.Cryptography;
using System.Text;

namespace Kintsugi.Application.ScriptApproval;

/// <summary>
/// Short, stable labels for a signing key and for a script's content — the two things the approval
/// repository is keyed by.
/// </summary>
public static class ScriptSignerFingerprint
{
    public const string Prefix = "sha256:";

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> over the SubjectPublicKeyInfo DER inside
    /// <paramref name="publicKeyPem"/> — the same thing ssh-keygen's <c>SHA256:</c> fingerprint
    /// digests, differing only in that this renders it as lowercase hex rather than base64.
    /// Hex because the fingerprint is also a path segment (see
    /// <c>ApprovedScriptCorpus.SignaturePath</c>): base64's <c>/</c> and <c>+</c> would need
    /// escaping in a git path, and carrying two spellings of the same fingerprint — one canonical,
    /// one filename-safe — is exactly the kind of drift that makes an entry unfindable.
    ///
    /// Computed straight off the PEM's base64 body rather than by importing the key, so this stays
    /// a pure function of the text and needs nothing from the Infrastructure layer.
    /// </summary>
    public static string For(string publicKeyPem)
    {
        var der = DecodePemBody(publicKeyPem);
        return Prefix + Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant();
    }

    /// <summary>The fingerprint without its <see cref="Prefix"/>, for use as a filename.</summary>
    public static string Bare(string fingerprint) =>
        fingerprint.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? fingerprint[Prefix.Length..]
            : fingerprint;

    /// <summary>
    /// The DER bytes out of a PEM block — every line that isn't a <c>-----BEGIN/END-----</c>
    /// delimiter, base64-decoded. Tolerates CRLF and trailing whitespace because this text has
    /// travelled through a JSON document and a git checkout to get here.
    /// </summary>
    private static byte[] DecodePemBody(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new ArgumentException("A PEM public key is required.", nameof(pem));
        }

        var body = new StringBuilder();
        foreach (var line in pem.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("-----", StringComparison.Ordinal))
            {
                continue;
            }

            body.Append(trimmed);
        }

        return Convert.FromBase64String(body.ToString());
    }
}

/// <summary>
/// The identity of a script, for every purpose in the approval flow: the approval repository's
/// directory name, the key an imported entry is stored under, and what a local upgrade path's script
/// is compared against to find out whether those exact bytes have already been reviewed.
/// </summary>
public static class ScriptContentHash
{
    /// <summary>Lowercase hex SHA-256 of <paramref name="script"/>'s UTF-8 bytes — the same bytes
    /// <c>ArtifactSigningService.Sign</c> signs, so the hash and the signature always describe the
    /// same thing.</summary>
    public static string Of(string script) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant();
}
