using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// The latest known version of an application, and how to upgrade an existing installation to it,
/// for one platform family (see <see cref="Platform"/>). Keyed by (<see cref="ApplicationName"/>,
/// <see cref="Platform"/>) — the same application can need different instructions on different
/// platforms, so each combination is researched and stored independently.
/// </summary>
public class UpgradePath : BaseEntity
{
    public string ApplicationName { get; private set; } = default!;

    /// <summary>"macOS", "Windows", "Linux", or "generic" for a platform-agnostic result (e.g. a
    /// package-manager command that behaves the same everywhere that manager runs).</summary>
    public string Platform { get; private set; } = default!;

    /// <summary>Whether this row represents a resolved answer, a confirmed "nothing found", or a
    /// failed attempt to check. A scan skips re-researching anything already <see cref="UpgradePathStatus.Found"/>.</summary>
    public UpgradePathStatus Status { get; private set; }

    public string? LatestVersion { get; private set; }
    public UpgradeMethod Method { get; private set; }
    public string? DownloadUrl { get; private set; }
    public string? Command { get; private set; }
    public string? Instructions { get; private set; }
    public string? SourceUrl { get; private set; }

    /// <summary>Freeform observation from whoever (or whatever) last researched this — e.g. an
    /// unfamiliar package manager, a failure reason, or a lack of reliable version information —
    /// for a human to review.</summary>
    public string? Notes { get; private set; }

    /// <summary>An executable script that performs this upgrade unattended on <see cref="Platform"/>
    /// — e.g. a bash script for "macOS" — for a management agent to fetch and run: either AI-authored
    /// for a resolved (<see cref="UpgradePathStatus.Found"/>) AI research result, or a fixed,
    /// deterministic one the server writes itself for a recognized package manager (e.g. Homebrew —
    /// see <c>HomebrewUpgradeScript.Build</c>). Null for an
    /// unrecognized package manager, an unresolved path, or a platform script generation doesn't yet
    /// support.</summary>
    public string? Script { get; private set; }

    /// <summary>The application bundle's identifier (CFBundleIdentifier on macOS) this path was
    /// researched for, when known — the key a generated <see cref="Script"/> is served under (as
    /// "{ApplicationIdentifier}.sh") rather than an anonymous file, and the value the script itself
    /// expects as its own `--appId` argument. For a package-manager-managed path there's no real
    /// bundle identifier to use (Homebrew formulae/casks don't have one), so this is the package
    /// name itself — an opaque value satisfying the script's CLI contract, not a real bundle ID.</summary>
    public string? ApplicationIdentifier { get; private set; }

    public DateTimeOffset CheckedUtc { get; private set; }

    /// <summary>Base64 ECDSA signature over <see cref="Script"/>'s UTF-8 bytes, produced by the
    /// server's own artifact-signing key (see <c>IArtifactSigningService</c>) only once a human has
    /// reviewed the script and explicitly signed it (see <see cref="SignScript"/>) — never set
    /// automatically by <see cref="Create"/> or <see cref="Update"/>, so a freshly
    /// AI-generated or hand-pasted script always starts out unsigned. The agent verifies this
    /// against its pinned copy of that key before ever executing a script — so an unsigned (or
    /// unreviewed) script is one no agent will run, rather than one silently trusted. Null whenever
    /// <see cref="Script"/> is, or whenever it hasn't been signed yet.</summary>
    public string? ScriptSignature { get; private set; }

    /// <summary>Same as <see cref="ScriptSignature"/>, but over <see cref="Command"/> — the other
    /// field an agent executes unattended (a package-manager one-liner). Null exactly when
    /// <see cref="Command"/> is.</summary>
    public string? CommandSignature { get; private set; }

    private UpgradePath()
    {
    }

    public static UpgradePath Create(
        string applicationName,
        string platform,
        UpgradePathStatus status,
        string? latestVersion,
        UpgradeMethod method,
        string? downloadUrl,
        string? command,
        string? instructions,
        string? sourceUrl,
        string? notes,
        string? script = null,
        string? applicationIdentifier = null)
    {
        var entity = new UpgradePath();
        entity.Apply(applicationName, platform, status, latestVersion, method, downloadUrl, command, instructions, sourceUrl, notes, script, applicationIdentifier);
        return entity;
    }

    public void Update(
        UpgradePathStatus status,
        string? latestVersion,
        UpgradeMethod method,
        string? downloadUrl,
        string? command,
        string? instructions,
        string? sourceUrl,
        string? notes,
        string? script = null,
        string? applicationIdentifier = null)
    {
        Apply(ApplicationName, Platform, status, latestVersion, method, downloadUrl, command, instructions, sourceUrl, notes, script, applicationIdentifier);
        MarkUpdated();
    }

    /// <summary>Records a fresh latest-version reading from an agent's own `--update-version`
    /// check — the whole point of a durable, reusable <see cref="Script"/> is that this can happen
    /// indefinitely without spending another AI research call. Touches only
    /// <see cref="LatestVersion"/> and <see cref="CheckedUtc"/>; everything else (Method, Script,
    /// Instructions, Notes, Status) is left exactly as the AI last resolved it, since none of that
    /// changes just because a newer version number was observed.</summary>
    public void UpdateDiscoveredLatestVersion(string? latestVersion)
    {
        LatestVersion = string.IsNullOrWhiteSpace(latestVersion) ? null : latestVersion;
        CheckedUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    /// <summary>Records the signatures an <c>IArtifactSigningService</c> computed over this row's
    /// current <see cref="Script"/>/<see cref="Command"/> — called immediately after
    /// <see cref="Create"/> or <see cref="Update"/> by the same command handler, so the two are
    /// always saved together and a row is never persisted with content that doesn't match its own
    /// signature.</summary>
    public void SetSignatures(string? scriptSignature, string? commandSignature)
    {
        ScriptSignature = scriptSignature;
        CommandSignature = commandSignature;
    }

    /// <summary>Records a human-approved signature over this row's current <see cref="Script"/> —
    /// the only way <see cref="ScriptSignature"/> gets set now that script signing requires a
    /// human in the loop (the Applications page's "Sign Script" action, after reviewing the
    /// result) rather than happening automatically alongside <see cref="Create"/> or
    /// <see cref="Update"/>. Leaves <see cref="CommandSignature"/> untouched.</summary>
    public void SignScript(string scriptSignature)
    {
        ScriptSignature = scriptSignature;
    }

    /// <summary>
    /// Takes on a script another server's reviewer already approved, and this server's own signature
    /// over it — the "adopt" half of the Upgrade Scripts page (see
    /// <c>AdoptApprovedScriptCommandHandler</c>). Re-signed locally rather than carrying the original
    /// signature across, because every agent pins exactly one signing key at enrollment: its own
    /// server's. A remote signature would be genuine and still refused by every agent that saw it.
    /// </summary>
    /// <remarks>
    /// Refuses outright once <see cref="ScriptSignature"/> is set. A row that already carries a valid
    /// signature is one agents may be executing right now, and replacing its content out from under
    /// them — with content from a repository, on a schedule nobody watched — is the one thing
    /// adoption must never do. Re-approving such a row is a deliberate act that goes back through
    /// <c>SignUpgradePathScriptCommand</c> and a human.
    /// </remarks>
    public void AdoptApprovedScript(string script, string scriptSignature, string? applicationIdentifier)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new DomainException("An approved script cannot be empty.");
        }

        if (ScriptSignature is not null)
        {
            throw new DomainException(
                $"'{ApplicationName}' on '{Platform}' already has an approved script; adopting another would replace "
                + "content agents may already be running. Review and sign a replacement instead.");
        }

        Script = script;
        ScriptSignature = scriptSignature;
        Status = UpgradePathStatus.Found;
        Method = UpgradeMethod.Script;
        if (!string.IsNullOrWhiteSpace(applicationIdentifier) && string.IsNullOrWhiteSpace(ApplicationIdentifier))
        {
            // Only filled in when this row hasn't got one. A local identifier came from an agent
            // actually reporting that installation, which is better evidence than the approving
            // server's note of what it happened to be reviewing.
            ApplicationIdentifier = applicationIdentifier;
        }
        CheckedUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    private void Apply(
        string applicationName,
        string platform,
        UpgradePathStatus status,
        string? latestVersion,
        UpgradeMethod method,
        string? downloadUrl,
        string? command,
        string? instructions,
        string? sourceUrl,
        string? notes,
        string? script,
        string? applicationIdentifier)
    {
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            throw new DomainException("Application name is required.");
        }

        if (string.IsNullOrWhiteSpace(platform))
        {
            throw new DomainException("Platform is required.");
        }

        ApplicationName = applicationName;
        Platform = platform;
        Status = status;
        LatestVersion = string.IsNullOrWhiteSpace(latestVersion) ? null : latestVersion;
        Method = method;
        DownloadUrl = string.IsNullOrWhiteSpace(downloadUrl) ? null : downloadUrl;
        Command = string.IsNullOrWhiteSpace(command) ? null : command;
        Instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions;
        SourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        Script = string.IsNullOrWhiteSpace(script) ? null : script;
        // An update that doesn't know the identifier (e.g. a package-manager path, which never
        // looks one up) keeps whatever was already recorded rather than wiping it — this only
        // ever gets cleared by explicitly passing an empty one, not by omission.
        if (!string.IsNullOrWhiteSpace(applicationIdentifier))
        {
            ApplicationIdentifier = applicationIdentifier;
        }
        CheckedUtc = DateTimeOffset.UtcNow;
    }
}
