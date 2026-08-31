using System.Text.Json;
using System.Text.Json.Serialization;
using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Application.ScriptApproval;

/// <summary>
/// The on-disk shape of the shared approval repository, and the only place its layout is described.
/// Both ends of the round trip use it — <c>IScriptApprovalPublisher</c> writing an entry when a human
/// signs a script, and <c>ImportApprovedScriptsFromSourceCommandHandler</c> reading it back on another
/// server — so the two cannot drift into disagreeing about where a file lives or what is in it.
/// </summary>
/// <remarks>
/// The layout is content-addressed:
/// <code>
/// approved-scripts/&lt;sha256&gt;/script.sh | script.ps1
/// approved-scripts/&lt;sha256&gt;/metadata.json
/// approved-scripts/&lt;sha256&gt;/signatures/&lt;fingerprint&gt;.json
/// </code>
/// Content-addressed because a package-manager script is byte-identical for every application that
/// manager handles (see each <c>*UpgradeScript.Build</c>, and <c>FindExistingSignatureForScriptAsync</c>
/// for the same idea applied locally): one review covers all of them, so keying by application would
/// store the same bytes hundreds of times and ask for the same review hundreds of times.
///
/// One file per signer under <c>signatures/</c>, rather than a list inside <c>metadata.json</c>,
/// because two servers approving the same script produce the same directory. Separate files mean
/// their pull requests never touch the same path and so never conflict; a shared array would
/// conflict every time.
/// </remarks>
public static class ApprovedScriptCorpus
{
    public const string RootDirectory = "approved-scripts";
    public const string MetadataFileName = "metadata.json";
    public const string SignaturesDirectory = "signatures";

    /// <summary>
    /// Written and parsed with indentation and a fixed property order (the record's own declaration
    /// order) so that approving identical content twice produces byte-identical files. That is what
    /// makes the publisher idempotent — it can compare what it is about to write against what is
    /// already on the default branch and skip opening a pull request that would change nothing.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string ContentDirectory(string sha256) => $"{RootDirectory}/{sha256}";

    public static string MetadataPath(string sha256) => $"{ContentDirectory(sha256)}/{MetadataFileName}";

    public static string ScriptPath(string sha256, ScriptLanguage language) =>
        $"{ContentDirectory(sha256)}/script{language.FileExtension()}";

    /// <summary>The signature file for one signer. <paramref name="fingerprint"/> arrives in the
    /// canonical <c>sha256:&lt;hex&gt;</c> form; the <c>sha256:</c> prefix is dropped for the
    /// filename because a colon is legal in a git path but awkward in a checkout on Windows.</summary>
    public static string SignaturePath(string sha256, string fingerprint) =>
        $"{ContentDirectory(sha256)}/{SignaturesDirectory}/{ScriptSignerFingerprint.Bare(fingerprint)}.json";

    public static string Serialize<T>(T document) => JsonSerializer.Serialize(document, JsonOptions) + "\n";

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);
}

/// <summary>
/// <c>approved-scripts/&lt;sha256&gt;/metadata.json</c> — what the script is for, so a server reading
/// the corpus can offer it as an adoption candidate without having to guess.
/// </summary>
/// <param name="Sha256">Lowercase hex SHA-256 of the script's UTF-8 bytes. Redundant with the
/// directory name on purpose: an importer checks the two agree, so a file moved into the wrong
/// directory is a rejected entry rather than a silently mislabelled one.</param>
/// <param name="PlatformBucket">The <c>PlatformBucket</c> the signing server stored it under.</param>
/// <param name="Language">Bash or PowerShell. Also derivable from the bucket via
/// <c>ScriptLanguages.For</c>, and an importer checks it matches — a bash script claiming a Windows
/// bucket is the exact shape of the bug the <c>generic</c> bucket used to allow.</param>
/// <param name="ApplicationName">The application the signer was reviewing. Informational for a
/// content-addressed entry, but it is what the Upgrade Scripts page offers to adopt.</param>
/// <param name="ApplicationIdentifier">The identifier the signing server had recorded, if any —
/// carried across because a Script row only patches at all when one is present (see
/// <c>is_patchable</c>).</param>
public record ApprovedScriptMetadataDocument(
    string Sha256,
    string PlatformBucket,
    ScriptLanguage Language,
    string ApplicationName,
    string? ApplicationIdentifier);

/// <summary>
/// <c>approved-scripts/&lt;sha256&gt;/signatures/&lt;fingerprint&gt;.json</c> — one signer's
/// attestation over the script's bytes.
/// </summary>
/// <param name="Sha256">Which content this signs, again redundantly with the path for the same
/// reason as <see cref="ApprovedScriptMetadataDocument.Sha256"/>.</param>
/// <param name="SignerFingerprint">Canonical <c>sha256:&lt;hex&gt;</c> over
/// <paramref name="SignerPublicKeyPem"/>'s SubjectPublicKeyInfo DER.</param>
/// <param name="SignerPublicKeyPem">The signing server's artifact-signing public key. Present so an
/// importer can check the signature at all — see <c>ApprovedScript.SignerPublicKeyPem</c> for a
/// precise statement of what that check does and does not prove.</param>
/// <param name="Signature">Base64 ECDSA-SHA256, DER-encoded (the ASN.1 SEQUENCE{r,s}), over the
/// script's UTF-8 bytes — the same format <c>ArtifactSigningService.Sign</c> emits and every agent
/// already verifies.</param>
/// <param name="SignedBy">The authenticated admin who reviewed it, for the audit trail.</param>
/// <param name="ServerFingerprintNote">Free text the signing server adds about itself. Never used
/// for a decision; it exists so a human reading a pull request can tell which deployment it came
/// from without cross-referencing fingerprints.</param>
public record ApprovedScriptSignatureDocument(
    string Sha256,
    string SignerFingerprint,
    string SignerPublicKeyPem,
    string Signature,
    string? SignedBy,
    DateTimeOffset SignedAtUtc,
    string? ServerFingerprintNote = null);
