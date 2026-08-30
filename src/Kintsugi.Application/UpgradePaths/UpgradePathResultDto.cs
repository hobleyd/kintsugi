using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// One (application, platform) upgrade path's researched fields, shaped identically whether it
/// came back from an AI check or was hand-entered — the Applications page's per-row panel shows
/// this JSON for an already-resolved path and accepts the same shape pasted back in to save
/// directly via <c>SaveUpgradePathCommand</c>, without going through the AI at all.
/// </summary>
public record UpgradePathResultDto(
    string ApplicationName,
    string Platform,
    UpgradePathStatus Status,
    string? LatestVersion,
    UpgradeMethod Method,
    string? DownloadUrl,
    string? Command,
    string? Instructions,
    string? SourceUrl,
    string? Notes,
    DateTimeOffset CheckedUtc,
    string? Script = null,
    bool ScriptSigned = false);
