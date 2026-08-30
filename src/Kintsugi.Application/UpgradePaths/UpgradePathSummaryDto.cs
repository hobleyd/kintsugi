using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// One researched (application, platform) upgrade path, with hosts aggregated into counts rather
/// than listed individually — this is what the Applications page renders, sized to the number of
/// distinct applications rather than the number of hosts running them. <see cref="HostNamesNeedingUpdate"/>
/// is the one exception: the Applications page's "filter by host" needs to know exactly which
/// hosts are behind on THIS application specifically, since "Update Available" as a status is
/// otherwise fleet-wide (true if any host anywhere is behind) and would otherwise make a combined
/// host + status filter show every app a host has installed that anyone is behind on, not just
/// the ones that host itself needs to update.
/// </summary>
public record UpgradePathSummaryDto(
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
    int HostCount,
    int UpToDateHostCount,
    int UpdateAvailableHostCount,
    IReadOnlyList<string> HostNamesNeedingUpdate,
    string? Script = null,
    string? ScriptSignature = null);
