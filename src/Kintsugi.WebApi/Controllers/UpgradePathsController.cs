using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.ReportDiscoveredVersion;
using Kintsugi.Application.UpgradePaths.Commands.SaveUpgradePath;
using Kintsugi.Application.UpgradePaths.Commands.SignUpgradePathScript;
using Kintsugi.Application.UpgradePaths.Commands.StartUpdateCheck;
using Kintsugi.Application.UpgradePaths.Commands.StartUpgradePathRefresh;
using Kintsugi.Application.UpgradePaths.Commands.StartUpgradePathScan;
using Kintsugi.Application.UpgradePaths.Queries.GetUpdateCheckStatus;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathPrompt;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathRefreshStatus;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScanStatus;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathSummaries;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradeStatuses;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

[ApiController]
[Route("api/upgrade-paths")]
[Produces("application/json")]
public class UpgradePathsController : ControllerBase
{
    private readonly ISender _sender;

    public UpgradePathsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// Lists one host's installed applications alongside their latest known upgrade path —
    /// installed version, latest version, and how to upgrade. This is how the kintsugi-agent asks
    /// what it should upgrade and how.
    /// </summary>
    [HttpGet]
    [RequireAgentIdentity]
    [ProducesResponseType(typeof(IReadOnlyList<UpgradeStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<UpgradeStatusDto>>> Get([FromQuery] string serialNumber, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetUpgradeStatusesQuery(serialNumber), cancellationToken));

    /// <summary>
    /// Lists every researched (application, platform) upgrade path fleet-wide, with host counts
    /// aggregated in rather than listed one row per host. Backs the Applications page's table.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(IReadOnlyList<UpgradePathSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UpgradePathSummaryDto>>> GetSummary(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetUpgradePathSummariesQuery(), cancellationToken));

    /// <summary>
    /// Starts a background scan resolving upgrade paths for every distinct installed application
    /// not yet resolved — via the configured AI agent where a script needs generating, a Homebrew
    /// command where one is package-manager-managed, or an instant "unsupported platform" note
    /// otherwise — working through them one at a time. Returns immediately — poll
    /// <c>scan-status</c> for progress. Backs the "Find Upgrade Paths" button; a second call while
    /// one is already running reports <c>started: false</c> rather than queuing a duplicate. Does
    /// not touch anything already resolved via a script — see <see cref="StartUpdateCheck"/> for that.
    /// </summary>
    [HttpPost("scan")]
    [ProducesResponseType(typeof(StartUpgradePathScanResult), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<StartUpgradePathScanResult>> StartScan(CancellationToken cancellationToken) =>
        Accepted(await _sender.Send(new StartUpgradePathScanCommand(), cancellationToken));

    /// <summary>Live progress of the current (or most recently completed) scan.</summary>
    [HttpGet("scan-status")]
    [ProducesResponseType(typeof(UpgradePathScanStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UpgradePathScanStatusDto>> GetScanStatus(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetUpgradePathScanStatusQuery(), cancellationToken));

    /// <summary>
    /// Starts a background "Check for Updates" run: re-checks every already-resolved script
    /// upgrade path by running its own <c>--update-version</c> mode — no AI call. Returns
    /// immediately — poll <c>check-updates-status</c> for progress. Backs the "Check for Updates"
    /// button; a second call while one is already running reports <c>started: false</c> rather
    /// than queuing a duplicate.
    /// </summary>
    [HttpPost("check-updates")]
    [ProducesResponseType(typeof(StartUpdateCheckResult), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<StartUpdateCheckResult>> StartUpdateCheck(CancellationToken cancellationToken) =>
        Accepted(await _sender.Send(new StartUpdateCheckCommand(), cancellationToken));

    /// <summary>Live progress of the current (or most recently completed) "Check for Updates" run.</summary>
    [HttpGet("check-updates-status")]
    [ProducesResponseType(typeof(UpdateCheckStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UpdateCheckStatusDto>> GetUpdateCheckStatus(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetUpdateCheckStatusQuery(), cancellationToken));

    /// <summary>
    /// Starts a background refresh for one application — either a single platform, or every
    /// platform it's installed on when <see cref="RefreshUpgradePathRequest.Platform"/> is omitted.
    /// Returns immediately; poll <see cref="GetRefreshStatus"/> for progress and the eventual
    /// result. Unlike <see cref="StartScan"/>, this forces a fresh check even for a path already
    /// known to be up to date, and a second call for an application already refreshing reports
    /// <c>started: false</c> rather than queuing a duplicate. Backs the per-row instructions panel
    /// on the Applications page — <see cref="RefreshUpgradePathRequest.Instructions"/> carries the
    /// (possibly hand-edited) prompt shown there.
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(StartUpgradePathRefreshResult), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<StartUpgradePathRefreshResult>> Refresh([FromBody] RefreshUpgradePathRequest request, CancellationToken cancellationToken) =>
        Accepted(await _sender.Send(new StartUpgradePathRefreshCommand(request.ApplicationName, request.Platform, request.Instructions), cancellationToken));

    /// <summary>Live progress of one application's background refresh (or its most recently
    /// completed result), polled by the Applications page after starting one via <see cref="Refresh"/>.</summary>
    [HttpGet("refresh-status")]
    [ProducesResponseType(typeof(UpgradePathRefreshStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UpgradePathRefreshStatusDto>> GetRefreshStatus([FromQuery] string applicationName, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetUpgradePathRefreshStatusQuery(applicationName), cancellationToken));

    /// <summary>
    /// The default AI prompt for one application's upgrade path research, without actually
    /// running it — backs the Applications page's per-row instructions panel, letting a user
    /// review or hand-edit it before triggering <see cref="Refresh"/>.
    /// </summary>
    [HttpGet("prompt")]
    [ProducesResponseType(typeof(UpgradePathPromptDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UpgradePathPromptDto>> GetPrompt([FromQuery] string applicationName, [FromQuery] string? platform, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetUpgradePathPromptQuery(applicationName, platform), cancellationToken));

    /// <summary>
    /// Saves a hand-entered (or pasted-in) upgrade path directly, bypassing the AI entirely.
    /// Backs the "Save Script" action on the Applications page's per-row panel — the same JSON shape
    /// shown there for an already-resolved path, or returned by <see cref="Refresh"/>, can be
    /// pasted back in here to persist it as-is.
    ///
    /// Requires a signed-in administrator (see <see cref="RequireAdminSessionAttribute"/>) — this
    /// accepts arbitrary script content, and paired with <see cref="SignScript"/> it is the whole
    /// path from "anything" to "content every agent in the fleet runs as root".
    /// </summary>
    [HttpPost("save")]
    [RequireAdminSession]
    [ProducesResponseType(typeof(UpgradePathResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UpgradePathResultDto>> Save([FromBody] SaveUpgradePathRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new SaveUpgradePathCommand(
            request.ApplicationName, request.Platform, request.LatestVersion, request.Method,
            request.DownloadUrl, request.Command, request.Instructions, request.SourceUrl, request.Notes, request.Script), cancellationToken));

    /// <summary>
    /// Signs one already-saved upgrade path's script with the server's artifact-signing key, after
    /// a human has reviewed it. Backs the "Sign Script" action on the Applications page's per-row
    /// panel — script signing never happens automatically (see <see cref="Refresh"/> and
    /// <see cref="Save"/>), only here, once a person has looked at the result.
    ///
    /// Also publishes the approval to the shared approval repository as a pull request, so the
    /// decision is recorded durably and other servers can adopt it (see
    /// <c>IScriptApprovalPublisher</c>). That is best-effort: a publication failure is reported in the
    /// response, not raised, because the local approval is already valid.
    ///
    /// Requires a signed-in administrator (see <see cref="RequireAdminSessionAttribute"/>). Without
    /// that, this route — the single point at which a human's review is recorded — was callable by
    /// anyone who could reach the server.
    /// </summary>
    [HttpPost("sign-script")]
    [RequireAdminSession]
    [ProducesResponseType(typeof(UpgradePathResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UpgradePathResultDto>> SignScript([FromBody] SignUpgradePathScriptRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(
            // The reviewer's own name, recorded in the approval entry and shown in the pull request —
            // "a human reviewed this" is only an audit trail if it says which human. Null when the
            // site is deliberately running with authentication disabled.
            new SignUpgradePathScriptCommand(request.ApplicationName, request.Platform, SignedBy: User.Identity?.Name),
            cancellationToken));

    /// <summary>
    /// Records a version an agent discovered by running its already-generated upgrade script's own
    /// `--update-version` mode locally — no AI call involved. This is what lets an upgrade path's
    /// known latest version stay current indefinitely once a script exists, instead of only ever
    /// updating via an expensive fresh AI research run.
    /// </summary>
    [HttpPost("report-version")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ReportVersion([FromBody] ReportDiscoveredVersionRequest request, CancellationToken cancellationToken)
    {
        await _sender.Send(new ReportDiscoveredVersionCommand(request.ApplicationName, request.Platform, request.LatestVersion), cancellationToken);
        return NoContent();
    }
}

public record RefreshUpgradePathRequest(string ApplicationName, string? Platform, string? Instructions = null);

public record SignUpgradePathScriptRequest(string ApplicationName, string Platform);

public record ReportDiscoveredVersionRequest(string ApplicationName, string Platform, string? LatestVersion);

public record SaveUpgradePathRequest(
    string ApplicationName,
    string Platform,
    string? LatestVersion,
    UpgradeMethod Method,
    string? DownloadUrl,
    string? Command,
    string? Instructions,
    string? SourceUrl,
    string? Notes,
    string? Script = null);
