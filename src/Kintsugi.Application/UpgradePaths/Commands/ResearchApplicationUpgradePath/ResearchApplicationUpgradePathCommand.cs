using MediatR;
using Kintsugi.Application.AiSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths.Commands.ResearchApplicationUpgradePath;

/// <summary>
/// Resolves the upgrade path for one (application, platform) combination — the single unit of
/// work a scan fans out across many concurrent, independently-scoped executions. Never throws:
/// any failure (network, parsing, anything unexpected) is caught, persisted as
/// <see cref="UpgradePathStatus.Failed"/>, and reported back rather than propagated, so one bad
/// application can't take down the rest of a scan.
/// </summary>
public record ResearchApplicationUpgradePathCommand(
    string ApplicationName,
    string Platform,
    IReadOnlyList<string> KnownVersions,
    UpgradePathWorkKind Kind,
    string? PackageManagerName,
    string? ApplicationIdentifier,
    // Null when the AI agent isn't configured or enabled — fine for PackageManagerManaged /
    // PackageManagerSelfUpdate work, which never calls the AI; Research work with null Settings
    // resolves to NotFound with a note instead of attempting a call.
    AiProviderSettings? Settings,
    // A fleet-wide scan skips anything already Found to avoid re-spending AI calls on known-good
    // paths; a user-triggered single-item refresh sets this to force a fresh check regardless.
    bool ForceRecheck = false,
    // Hand-edited prompt text from the Applications page's per-row instructions panel, replacing
    // the default AI prompt wholesale. Ignored for anything resolved via a package manager, which
    // never calls the AI at all.
    string? PromptOverride = null) : IRequest<ResearchApplicationUpgradePathResult>;

public record ResearchApplicationUpgradePathResult(
    string ApplicationName,
    string Platform,
    UpgradePathStatus Status,
    bool Skipped,
    string? Note,
    string? LatestVersion = null,
    UpgradeMethod Method = UpgradeMethod.Unknown,
    string? DownloadUrl = null,
    string? Command = null,
    string? Instructions = null,
    string? SourceUrl = null,
    DateTimeOffset CheckedUtc = default,
    string? Script = null);
