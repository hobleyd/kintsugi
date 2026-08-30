using MediatR;

namespace Kintsugi.Application.UpgradePaths.Commands.ReportDiscoveredVersion;

/// <summary>
/// Records a version an agent discovered by running an already-generated upgrade script's own
/// `--update-version` mode locally — no AI call involved. This is what lets the Applications
/// page's "latest version" stay current indefinitely after a script exists, instead of going
/// stale until someone forces a fresh (expensive) AI research run.
/// </summary>
public record ReportDiscoveredVersionCommand(string ApplicationName, string Platform, string? LatestVersion) : IRequest<Unit>;
