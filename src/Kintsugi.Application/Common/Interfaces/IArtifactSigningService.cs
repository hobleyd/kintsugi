namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Signs the content an agent will execute unattended (an <c>UpgradePath</c>'s Script/Command)
/// with a key dedicated to that purpose alone — distinct from <see cref="ICaService"/>'s CA key,
/// which grants agent *identity* rather than vouching for specific content. A compromise of the
/// database (or a direct write that skips the normal save path) produces content with no valid
/// signature, which every agent refuses to run — see the kintsugi-agent's own verification before
/// <c>patch_one</c>.
/// </summary>
public interface IArtifactSigningService
{
    /// <summary>PEM-encoded public key — handed to an agent at enrollment (see
    /// <c>EnrollAgentCommandHandler</c>) for it to pin and verify every future signature against.</summary>
    string GetPublicKeyPem();

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> over <see cref="GetPublicKeyPem"/>'s SubjectPublicKeyInfo DER —
    /// this server's identity as a script signer in the shared approval repository (see
    /// <c>ApprovedScriptCorpus</c>), and the one fingerprint whose signatures a server can regard as
    /// genuinely verified rather than merely self-consistent, since the key behind it never left the
    /// api-only private volume.
    /// </summary>
    string GetPublicKeyFingerprint();

    /// <summary>Base64 ECDSA-SHA256 signature over <paramref name="content"/>'s UTF-8 bytes, or
    /// null when <paramref name="content"/> itself is null/empty — there's nothing to sign, and no
    /// signature to check against an absent field.</summary>
    string? Sign(string? content);
}
