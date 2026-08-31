namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Checks an approval entry's signature against the public key that entry carries.
/// </summary>
/// <remarks>
/// What this establishes, precisely: that the entry is internally consistent and names a signer. It
/// does <em>not</em> establish that the signer was authorized, because the public key travels in the
/// same repository as the script it vouches for — anyone able to write there can edit a script, mint
/// a fresh keypair, and produce an entry that verifies perfectly. Authorization comes from the
/// repository's own branch protection on the default branch, which is the configured trust root.
///
/// The one case this genuinely verifies is a fingerprint equal to
/// <c>IArtifactSigningService.GetPublicKeyFingerprint</c>: a signature this server itself produced,
/// checked against a key that never left its private volume.
/// </remarks>
public interface IScriptSignatureVerifier
{
    /// <summary>True when <paramref name="base64Signature"/> is a valid ECDSA-SHA256 (DER) signature
    /// over <paramref name="script"/>'s UTF-8 bytes under <paramref name="publicKeyPem"/>. False —
    /// never an exception — for a malformed key, a malformed signature, or a mismatch, since all
    /// three mean the same thing to the caller: don't import this entry.</summary>
    bool Verify(string script, string base64Signature, string publicKeyPem);
}
