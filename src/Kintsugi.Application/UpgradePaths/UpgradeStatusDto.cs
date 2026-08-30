using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// One host's installation of one application, combined with the latest known upgrade path for
/// it. This is what the Applications page renders and what the kintsugi-agent asks for when it
/// wants to know whether — and how — to upgrade something it has installed.
/// </summary>
/// <param name="Script">An executable script implementing a durable `--update-version`/`--update`
/// CLI — either AI-authored (see <see cref="Common.Interfaces.IUpgradePathResearchClient.GenerateScriptAsync"/>)
/// for this host's platform (currently only ever populated for "macOS"), or a fixed, deterministic
/// one the server writes itself for a recognized package manager (e.g. Homebrew) — for the agent to
/// run directly and repeatedly instead of interpreting <see cref="Instructions"/> itself or
/// re-asking the AI for every future version check. Null whenever no script exists — e.g. an
/// unrecognized package manager, an unresolved path, or a platform script generation doesn't
/// support yet.</param>
/// <param name="ApplicationIdentifier">The installed application's CFBundleIdentifier, when known
/// — the agent passes this as the script's required `--appId` argument.</param>
public record UpgradeStatusDto(
    string ApplicationName,
    string Hostname,
    string SerialNumber,
    string InstalledVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    UpgradePathStatus Status,
    UpgradeMethod Method,
    string? DownloadUrl,
    string? Command,
    string? Instructions,
    string? SourceUrl,
    string? Notes,
    DateTimeOffset? CheckedUtc,
    string? Script = null,
    string? ApplicationIdentifier = null,
    string? ScriptSignature = null,
    string? CommandSignature = null);
