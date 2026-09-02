using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths.Commands.SaveUpgradePath;

/// <summary>
/// Persists a hand-entered (or pasted-in) upgrade path for one application and platform, exactly
/// as if it had come back from the AI research flow. Backs the "Save Script" action on the
/// Applications page's per-row panel — the escape hatch for supplying or correcting an upgrade
/// path without depending on the AI agent at all.
///
/// There's no <c>Status</c> field: the AI response schema doesn't have one, so the handler
/// derives it from <see cref="Method"/> the same way the automated research flow does.
/// </summary>
public record SaveUpgradePathCommand(
    string ApplicationName,
    string Platform,
    string? LatestVersion,
    UpgradeMethod Method,
    string? DownloadUrl,
    string? Command,
    string? Instructions,
    string? SourceUrl,
    string? Notes,
    string? Script = null,
    string? ApplicationIdentifier = null) : IRequest<UpgradePathResultDto>;
