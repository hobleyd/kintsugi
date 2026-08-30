using Kintsugi.Application.AiSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>Uses the configured AI agent — with web search, where the provider supports it — to
/// generate a durable, self-checking upgrade script for an application, and to run that script's
/// own `--update-version` mode locally afterward so checking for new releases never needs another
/// AI call.</summary>
public interface IUpgradePathResearchClient
{
    /// <summary>
    /// Researches how <paramref name="request"/>'s application distributes and checks for updates,
    /// and produces a single durable script implementing the CLI contract described on
    /// <see cref="UpgradePathScriptGenerationRequest"/> — one AI call does both jobs; there is no
    /// separate research-then-script step. Throws if the call itself failed (network error, or the
    /// script still failed validation after one self-correction attempt) — callers should treat any
    /// exception as a transient failure worth retrying, as distinct from a successful
    /// <see cref="UpgradePathScriptResult"/> reporting <see cref="UpgradePathStatus.NotFound"/>,
    /// which is the model's own considered "no reliable method".
    /// </summary>
    Task<UpgradePathScriptResult> GenerateScriptAsync(AiProviderSettings settings, UpgradePathScriptGenerationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Builds the default prompt for <paramref name="request"/> without sending it anywhere —
    /// shown to a user in the Applications page's per-row instructions panel so they can review or
    /// hand-edit it before it's actually sent via <see cref="GenerateScriptAsync"/> (as
    /// <see cref="UpgradePathScriptGenerationRequest.PromptOverride"/>). Omits the GitHub/GitLab
    /// hosting context <see cref="GenerateScriptAsync"/> enriches the live prompt with, since that
    /// requires network calls this preview doesn't make.
    /// </summary>
    string BuildDefaultPrompt(UpgradePathScriptGenerationRequest request);

    /// <summary>
    /// Runs an already-generated script's own `--update-version` mode locally, server-side — no AI
    /// call involved. This is the whole point of generating a durable script instead of a one-shot
    /// answer: once a script exists, checking for a new release costs a subprocess call, not an AI
    /// call. The prompt requires `--update-version` to work under plain bash + curl with no
    /// platform-specific tooling precisely so this can run on the (Linux) server itself rather than
    /// needing an agent. Returns null on any failure — a non-zero exit, a timeout, empty output —
    /// so callers can treat that as "the script broke" and fall back to asking the AI to regenerate
    /// it, per the reason this method exists.
    /// </summary>
    Task<string?> CheckScriptVersionAsync(string script, string applicationName, string applicationIdentifier, CancellationToken cancellationToken);
}

/// <param name="KnownInstalledVersions">Distinct versions currently seen installed across managed
/// hosts on this platform, given to the model as context.</param>
/// <param name="ApplicationIdentifier">The app bundle's CFBundleIdentifier, when known — a
/// disambiguating search signal (e.g. distinguishing generically-named applications), used to look
/// the application up directly on GitHub/GitLab before the model is asked anything, and required by
/// the script's `--appId` argument as a safety check against acting on the wrong app.</param>
/// <param name="PromptOverride">Hand-edited prompt text from the Applications page's per-row
/// instructions panel, sent to the model verbatim in place of the default prompt when set.</param>
public record UpgradePathScriptGenerationRequest(
    string ApplicationName,
    string Platform,
    IReadOnlyList<string> KnownInstalledVersions,
    string? ApplicationIdentifier = null,
    string? PromptOverride = null);

/// <param name="Status">Never <see cref="UpgradePathStatus.Failed"/> — a failed call is an
/// exception, not a result; this is only ever <see cref="UpgradePathStatus.Found"/> (a usable
/// script was produced) or <see cref="UpgradePathStatus.NotFound"/> (the model's own considered "no
/// reliable method").</param>
/// <param name="Script">The generated script, when <paramref name="Status"/> is
/// <see cref="UpgradePathStatus.Found"/>; otherwise null.</param>
/// <param name="Notes">A short human-readable explanation, populated only when
/// <paramref name="Status"/> is <see cref="UpgradePathStatus.NotFound"/> — any caveat the model has
/// about a successfully generated script (e.g. reduced confidence from lacking live web access) is
/// instead written directly into the script as a leading comment, since the script itself is the
/// only thing callers keep.</param>
public record UpgradePathScriptResult(UpgradePathStatus Status, string? Script, string? Notes);
