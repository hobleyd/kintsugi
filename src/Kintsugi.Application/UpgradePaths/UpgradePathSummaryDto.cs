using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// One researched (application, platform) upgrade path, with hosts aggregated into counts rather
/// than listed individually — this is what the Applications page renders, sized to the number of
/// distinct applications rather than the number of hosts running them.
/// </summary>
/// <remarks>
/// <para><see cref="HostNames"/> and <see cref="HostNamesNeedingUpdate"/> are the two exceptions,
/// and they answer different questions for the Applications page's "filter by host" control.</para>
/// <para><see cref="HostNames"/> is which hosts resolved to THIS bucket. The application-level
/// <c>ApplicationRowDto.HostNames</c> is keyed on the application's name alone, so an application
/// installed from Homebrew on a Mac and from winget on a PC has one host list spanning both while
/// being two summary rows — and filtering on that list kept the <c>pm:Homebrew</c> row on screen
/// when a Windows host was chosen.</para>
/// <para><see cref="HostNamesNeedingUpdate"/> is which of those hosts are behind. "Update
/// Available" as a status is fleet-wide (true if any host anywhere is behind), so a combined host
/// + status filter testing installation alone would show every app a host has installed that
/// anyone is behind on, not just the ones that host itself needs to update.</para>
/// </remarks>
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
    IReadOnlyList<string> HostNames,
    IReadOnlyList<string> HostNamesNeedingUpdate,
    string? Script = null,
    string? ScriptSignature = null)
{
    /// <summary>
    /// The single status this row displays as, folding <see cref="Status"/>,
    /// <see cref="Method"/>, the signature and the per-host update counts into one value — see
    /// <see cref="UpgradePathStatusKey.For"/> for the precedence between them.
    /// </summary>
    /// <remarks>
    /// Serialized rather than left for the client to derive, because the rule is not obvious
    /// (an unsigned script outranks "update available", since an unsigned script means no agent
    /// runs it at all) and it also drives the status filter. The Flutter client re-deriving it
    /// would be a second copy free to disagree with the one the server uses.
    /// </remarks>
    public string StatusKey => UpgradePathStatusKey.For(this);
}
