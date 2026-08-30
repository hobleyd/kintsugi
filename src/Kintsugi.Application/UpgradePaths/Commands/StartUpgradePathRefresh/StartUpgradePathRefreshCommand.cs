using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Commands.StartUpgradePathRefresh;

/// <summary>
/// Starts a background refresh for one application — either a single platform, or every platform
/// it's installed on when <see cref="Platform"/> is <c>null</c> — and returns immediately. Backs
/// the per-row "Send to AI" action on the Applications page; poll
/// <see cref="Queries.GetUpgradePathRefreshStatus.GetUpgradePathRefreshStatusQuery"/> for progress
/// and the eventual result, since a local AI agent (e.g. Goose backed by a local model) can take
/// far longer than any single HTTP request should block for.
/// </summary>
/// <param name="PromptOverride">Hand-edited prompt text from the Applications page's per-row
/// instructions panel, sent to the AI verbatim in place of the default prompt.</param>
public record StartUpgradePathRefreshCommand(string ApplicationName, string? Platform, string? PromptOverride = null) : IRequest<StartUpgradePathRefreshResult>;

public record StartUpgradePathRefreshResult(bool Started, UpgradePathRefreshStatusDto Status);
