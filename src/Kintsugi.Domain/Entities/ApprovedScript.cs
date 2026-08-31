using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One upgrade script that some server's human reviewer signed and published to the shared approval
/// repository, as this server last read it back (see <c>ImportApprovedScriptsFromSourceCommandHandler</c>).
/// This is the *imported corpus*, not what agents run: an agent only ever executes an
/// <see cref="UpgradePath.Script"/> carrying a <see cref="UpgradePath.ScriptSignature"/> from this
/// server's own artifact-signing key, so a row here becomes runnable only once it has been blessed
/// or adopted onto an upgrade path and re-signed locally.
///
/// Keyed by (<see cref="Sha256"/>, <see cref="SignerFingerprint"/>) rather than by application,
/// because a package-manager script is byte-identical for every application that manager handles
/// (see each <c>*UpgradeScript.Build</c>) — one review covers all of them, which is the same reason
/// <c>FindExistingSignatureForScriptAsync</c> matches on content locally. Two different signers
/// approving the same bytes are two rows, so the page can show who vouched for what.
/// </summary>
public class ApprovedScript : BaseEntity
{
    /// <summary>Lowercase hex SHA-256 of <see cref="Script"/>'s UTF-8 bytes — the directory name
    /// this entry lives under in the approval repository, and what a local upgrade path's script is
    /// compared against to decide it has already been approved.</summary>
    public string Sha256 { get; private set; } = default!;

    /// <summary>The <c>PlatformBucket</c> the signing server had this script stored under — an OS
    /// bucket or a <c>pm:</c> one. Load-bearing for adoption: <c>ScriptLanguages.For</c> maps it to
    /// bash or PowerShell, and a mismatch is exactly the failure the <c>generic</c> bucket used to
    /// allow, where a Windows host was handed a genuinely-signed <c>#!/bin/bash</c> script.</summary>
    public string PlatformBucket { get; private set; } = default!;

    /// <summary>The script's text, byte-for-byte as it was signed. Any change to it invalidates
    /// <see cref="Signature"/>, which is the point.</summary>
    public string Script { get; private set; } = default!;

    /// <summary>The application the signing server happened to be reviewing when it approved these
    /// bytes. Informational for a content-addressed entry — a package-manager script's real scope is
    /// every application its manager handles, not this one — but it is what the Upgrade Scripts page
    /// offers as an adoption candidate.</summary>
    public string ApplicationName { get; private set; } = default!;

    /// <summary>The identifier the signing server recorded, when it had one. Carried across because
    /// a <c>Script</c> row is only patchable at all if the agent reported an application identifier
    /// for that installation — see <c>is_patchable</c>.</summary>
    public string? ApplicationIdentifier { get; private set; }

    /// <summary><c>SHA256:&lt;base64&gt;</c> over the signer's SubjectPublicKeyInfo DER, ssh-keygen
    /// style. This is an *attribution* label, shown to a human before they adopt anything — see the
    /// remark on <see cref="SignerPublicKeyPem"/> for why it is not an authorization check.</summary>
    public string SignerFingerprint { get; private set; } = default!;

    /// <summary>
    /// The PEM public key the approval entry carried, and the key <see cref="Signature"/> was
    /// checked against at import.
    ///
    /// Be precise about what that check proves: the key travels in the same repository as the bytes
    /// it vouches for, so anyone able to write to that repository can edit a script, mint a fresh
    /// keypair, and produce an entry that verifies perfectly. Verification therefore establishes
    /// that an entry is internally consistent and names its signer — not that the signer was
    /// authorized. The only genuinely verified case is a fingerprint matching this server's own
    /// artifact-signing key (see <c>IArtifactSigningService.GetPublicKeyFingerprint</c>), which is
    /// a signature this server itself produced.
    /// </summary>
    public string SignerPublicKeyPem { get; private set; } = default!;

    /// <summary>Base64 ECDSA-SHA256 (DER, the ASN.1 SEQUENCE{r,s} — matching
    /// <c>ArtifactSigningService.Sign</c>) over <see cref="Script"/>'s UTF-8 bytes.</summary>
    public string Signature { get; private set; } = default!;

    /// <summary>Who signed it, as recorded by the signing server — the authenticated admin's name,
    /// which is the audit half of "a human reviewed this".</summary>
    public string? SignedBy { get; private set; }

    /// <summary>When the signing server signed it, as recorded in the approval entry.</summary>
    public DateTimeOffset ApprovedAtUtc { get; private set; }

    /// <summary>The approval repository commit this server read the entry out of — the answer to
    /// "which merge put this on my server", which the default branch being the trust root makes the
    /// only provenance there is.</summary>
    public string SourceCommitSha { get; private set; } = default!;

    public DateTimeOffset ImportedAtUtc { get; private set; }

    private ApprovedScript()
    {
    }

    public static ApprovedScript Create(
        string sha256,
        string platformBucket,
        string script,
        string applicationName,
        string? applicationIdentifier,
        string signerFingerprint,
        string signerPublicKeyPem,
        string signature,
        string? signedBy,
        DateTimeOffset approvedAtUtc,
        string sourceCommitSha)
    {
        var entity = new ApprovedScript();
        entity.Apply(
            sha256, platformBucket, script, applicationName, applicationIdentifier, signerFingerprint,
            signerPublicKeyPem, signature, signedBy, approvedAtUtc, sourceCommitSha);
        return entity;
    }

    /// <summary>Re-reads an entry that is already stored, for the case where a later commit changed
    /// something about it that isn't part of the key — the application it was recorded against, say.
    /// The content and the signature can't change without changing <see cref="Sha256"/>, so this
    /// never silently swaps out what a human already looked at.</summary>
    public void Refresh(string applicationName, string? applicationIdentifier, string? signedBy, string sourceCommitSha)
    {
        ApplicationName = Require(applicationName, "An application name is required.");
        ApplicationIdentifier = NullIfBlank(applicationIdentifier);
        SignedBy = NullIfBlank(signedBy);
        SourceCommitSha = Require(sourceCommitSha, "A source commit sha is required.");
        ImportedAtUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    private void Apply(
        string sha256,
        string platformBucket,
        string script,
        string applicationName,
        string? applicationIdentifier,
        string signerFingerprint,
        string signerPublicKeyPem,
        string signature,
        string? signedBy,
        DateTimeOffset approvedAtUtc,
        string sourceCommitSha)
    {
        Sha256 = Require(sha256, "A script checksum is required.");
        PlatformBucket = Require(platformBucket, "A platform bucket is required.");
        Script = string.IsNullOrEmpty(script) ? throw new DomainException("A script is required.") : script;
        ApplicationName = Require(applicationName, "An application name is required.");
        ApplicationIdentifier = NullIfBlank(applicationIdentifier);
        SignerFingerprint = Require(signerFingerprint, "A signer fingerprint is required.");
        SignerPublicKeyPem = Require(signerPublicKeyPem, "A signer public key is required.");
        Signature = Require(signature, "A signature is required.");
        SignedBy = NullIfBlank(signedBy);
        ApprovedAtUtc = approvedAtUtc;
        SourceCommitSha = Require(sourceCommitSha, "A source commit sha is required.");
        ImportedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string Require(string value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException(message) : value;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
