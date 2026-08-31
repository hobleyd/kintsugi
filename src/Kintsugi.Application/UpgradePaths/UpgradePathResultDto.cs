using Kintsugi.Domain.Enums;
using Kintsugi.Application.ScriptApproval;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// One (application, platform) upgrade path's researched fields, shaped identically whether it
/// came back from an AI check or was hand-entered — the Applications page's per-row panel shows
/// this JSON for an already-resolved path and accepts the same shape pasted back in to save
/// directly via <c>SaveUpgradePathCommand</c>, without going through the AI at all.
/// </summary>
/// <param name="ApprovalOutcome">What became of publishing this approval to the shared approval
/// repository, when this result came from a signing. Null for every other path, since nothing was
/// published. A <c>Failed</c> or <c>Disabled</c> outcome here does not mean the signing failed — the
/// signature is saved regardless; see <c>SignUpgradePathScriptCommandHandler</c>.</param>
/// <param name="ApprovalPullRequestUrl">The pull request to go and get merged, when one was opened
/// or already existed.</param>
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
    bool ScriptSigned = false,
    ScriptApprovalPublishOutcome? ApprovalOutcome = null,
    string? ApprovalPullRequestUrl = null,
    string? ApprovalMessage = null);
