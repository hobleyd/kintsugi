using MediatR;
using Kintsugi.Application.AiSettings;

namespace Kintsugi.Application.UpgradePaths.Queries.PrepareUpgradePathScan;

/// <summary>
/// Builds the work plan for an upgrade-path scan: whether the AI agent is configured at all, and
/// the full list of (application, platform) combinations that need resolving. Read once, up
/// front, by the background scanner — the actual per-item work happens via
/// <c>ResearchApplicationUpgradePathCommand</c>, each running in its own scope so many can run
/// concurrently.
/// </summary>
public record PrepareUpgradePathScanQuery : IRequest<UpgradePathScanPlan>;

public record UpgradePathScanPlan(bool AiConfigured, AiProviderSettings? Settings, IReadOnlyList<UpgradePathWorkItem> WorkItems);
