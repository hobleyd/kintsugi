using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Application.Vulnerabilities.Commands.ConfirmCpeMapping;
using Kintsugi.Application.Vulnerabilities.Commands.ConfirmCpeMappings;
using Kintsugi.Application.Vulnerabilities.Commands.ResetCpeMapping;
using Kintsugi.Application.Vulnerabilities.Commands.ResetCpeMappings;
using Kintsugi.Application.Vulnerabilities.Commands.SetCpeMappingNotApplicable;
using Kintsugi.Application.Vulnerabilities.Queries.GetCpeMappings;
using Kintsugi.Application.Vulnerabilities.Queries.GetVulnerabilityOverview;
using Kintsugi.WebApi.Filters;
using Kintsugi.WebApi.Vulnerabilities;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The Vulnerabilities screen's data: which published CVEs affect the versions this fleet has
/// installed, and the CPE mapping queue that decides what can be assessed at all.
/// </summary>
/// <remarks>
/// <para>
/// Entirely browser-driven — no agent reads any of this — so it is under <c>/api/admin</c> and
/// outside nginx's exact-match agent-certificate regex, with
/// <see cref="RequireAdminSessionAttribute"/> <b>on the class</b>. That placement is the point:
/// <c>Program.cs</c> exempts all of <c>/api</c> from the sign-in gate on the reasoning that agents
/// authenticate with mutual TLS, so a route here that inherited no attribute would be anonymous.
/// See the root CLAUDE.md on the gap between the two mechanisms, and note that this controller has
/// mutating routes on it — confirming a CPE mapping decides which product's CVEs get attributed to
/// the fleet.
/// </para>
/// <para>
/// Each route also needs a <c>location</c> in <c>nginx/default.conf</c> above the SPA fallback, or
/// nginx answers it with <c>index.html</c> — a 200 full of markup, much harder to diagnose than a
/// 404.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/vulnerabilities")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminVulnerabilitiesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly IVulnerabilityRunCoordinator _coordinator;
    private readonly INvdClient _nvdClient;
    private readonly IVulnerabilitySettingsProvider _settingsProvider;

    public AdminVulnerabilitiesController(
        ISender sender,
        IVulnerabilityRunCoordinator coordinator,
        INvdClient nvdClient,
        IVulnerabilitySettingsProvider settingsProvider)
    {
        _sender = sender;
        _coordinator = coordinator;
        _nvdClient = nvdClient;
        _settingsProvider = settingsProvider;
    }

    /// <summary>
    /// The fleet's exposure: a summary, and the CVEs affecting it.
    /// </summary>
    /// <param name="knownExploitedOnly">Defaults to true — the exploited set is the part anybody
    /// can act on, and the total is reported as a number in the summary rather than as a list of
    /// tens of thousands of rows.</param>
    /// <param name="page">Zero-based. Clamped to the last page that exists, and the response says
    /// which page it actually returned.</param>
    /// <param name="pageSize">Rows per page, clamped to 500. The screen asks for 100.</param>
    /// <param name="sortKey">A <c>VulnerabilityFindingSort</c> value; anything else falls back to
    /// the default order rather than erroring.</param>
    /// <param name="sortAscending">Which way <paramref name="sortKey"/> runs.</param>
    /// <param name="cveSearch">Substring of the CVE id.</param>
    /// <param name="severity">A CVSS severity band, or "Unscored".</param>
    /// <param name="minScore">A CVSS base score floor. A threshold, not a band — unscored rows
    /// never pass it, because a missing score is not a low one.</param>
    /// <param name="platform">A <c>VulnerabilityPlatform</c> value.</param>
    /// <param name="subjectSearch">Substring of an affected product's name or version.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>The filters and the sort are applied here rather than in the browser because the
    /// page is cut after them: sorting a page client-side would order the page, not the set, and
    /// "the least-installed of the highest-scoring hundred" reads as the fleet's least-installed
    /// while being nothing of the kind.</remarks>
    [HttpGet]
    [ProducesResponseType(typeof(VulnerabilityOverviewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<VulnerabilityOverviewDto>> GetOverview(
        [FromQuery] bool knownExploitedOnly = true,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 100,
        [FromQuery] string? sortKey = null,
        [FromQuery] bool sortAscending = false,
        [FromQuery] string? cveSearch = null,
        [FromQuery] string? severity = null,
        [FromQuery] double? minScore = null,
        [FromQuery] string? platform = null,
        [FromQuery] string? subjectSearch = null,
        CancellationToken cancellationToken = default) =>
        Ok(await _sender.Send(
            new GetVulnerabilityOverviewQuery(
                knownExploitedOnly,
                Math.Max(page, 0),
                Math.Clamp(pageSize, 1, 500),
                sortKey,
                sortAscending,
                cveSearch,
                severity,
                minScore,
                platform,
                subjectSearch),
            cancellationToken));

    /// <summary>The CPE mapping queue — every subject the fleet has, and how far each has got.</summary>
    [HttpGet("mappings")]
    [ProducesResponseType(typeof(IReadOnlyList<CpeMappingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CpeMappingDto>>> GetMappings(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetCpeMappingsQuery(), cancellationToken));

    /// <summary>
    /// Candidate vendors and products from NVD's own CPE dictionary for a free-text name.
    /// </summary>
    /// <remarks>
    /// A weak signal offered to a human rather than adopted: the live dictionary ranks Slackware
    /// Linux first for "slack" and ZoomText for "zoom". It also spends this server's shared NVD
    /// rate allowance, which is why that limiter is a process-wide singleton — a search issued
    /// while an assessment run is in flight queues behind it rather than earning both a 403 (see
    /// <c>NvdRateLimiter</c>, in the Infrastructure project).
    /// </remarks>
    [HttpGet("cpe-dictionary")]
    [ProducesResponseType(typeof(IReadOnlyList<CpeCandidateDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CpeCandidateDto>>> SearchDictionary(
        [FromQuery] string keyword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return Ok(Array.Empty<CpeCandidateDto>());
        }

        var settings = await _settingsProvider.GetAsync(cancellationToken);
        var candidates = await _nvdClient.SearchCpeDictionaryAsync(keyword, settings.NvdApiKey, cancellationToken);

        return Ok(candidates
            .Select(c => new CpeCandidateDto(c.Part, c.Vendor, c.Product, c.TitleHint, c.EntryCount))
            .ToList());
    }

    /// <summary>Accepts a vendor and product for one subject. Answers 409 if NVD's dictionary
    /// contains no such product — a typo here silently attributes another product's CVEs to
    /// this one.</summary>
    [HttpPost("mappings/{id:guid}/confirm")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Confirm(Guid id, [FromBody] ConfirmCpeMappingRequest request, CancellationToken cancellationToken)
    {
        await _sender.Send(new ConfirmCpeMappingCommand(id, request.Vendor, request.Product), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Accepts what several subjects already propose, in one request — the queue's bulk Confirm.
    /// </summary>
    /// <remarks>
    /// Its own route rather than the UI calling <see cref="Confirm"/> in a loop, and the reason is
    /// NVD's rate limit: that route asks the dictionary whether the pair exists, which is right for
    /// a value somebody has just typed and pointless for a stored suggestion that was checked
    /// before it was written. Forty ticked rows would be forty NVD requests against an allowance of
    /// five per thirty seconds, most of them failing. Answers 200 with what was applied and what
    /// was skipped, never a partial failure the caller has to reassemble.
    /// </remarks>
    [HttpPost("mappings/confirm")]
    [ProducesResponseType(typeof(BulkCpeMappingResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BulkCpeMappingResultDto>> ConfirmMany(
        [FromBody] BulkCpeMappingRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new ConfirmCpeMappingsCommand(request.Ids ?? Array.Empty<Guid>()), cancellationToken));

    /// <summary>Returns several subjects to the mapping queue at once — the queue's bulk
    /// Clear.</summary>
    [HttpPost("mappings/reset")]
    [ProducesResponseType(typeof(BulkCpeMappingResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BulkCpeMappingResultDto>> ResetMany(
        [FromBody] BulkCpeMappingRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new ResetCpeMappingsCommand(request.Ids ?? Array.Empty<Guid>()), cancellationToken));

    /// <summary>Records that a subject has no meaningful CPE, taking it out of the "not assessed"
    /// count — a decision rather than a gap.</summary>
    [HttpPost("mappings/{id:guid}/not-applicable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> NotApplicable(
        Guid id, [FromBody] SetCpeMappingNotApplicableRequest request, CancellationToken cancellationToken)
    {
        await _sender.Send(new SetCpeMappingNotApplicableCommand(id, request.Notes), cancellationToken);
        return NoContent();
    }

    /// <summary>Returns a subject to the mapping queue, discarding whatever it was matched
    /// against.</summary>
    [HttpPost("mappings/{id:guid}/reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reset(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new ResetCpeMappingCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>What the last or current assessment run is doing.</summary>
    [HttpGet("run")]
    [ProducesResponseType(typeof(VulnerabilityRunStatusDto), StatusCodes.Status200OK)]
    public ActionResult<VulnerabilityRunStatusDto> GetRunStatus() => Ok(_coordinator.GetStatus());

    /// <summary>
    /// Starts an assessment now. Answers 409 rather than queueing when one is already running —
    /// two runs would spend one NVD rate allowance between them and collect 403s.
    /// </summary>
    [HttpPost("run")]
    [ProducesResponseType(typeof(VulnerabilityRunStatusDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<VulnerabilityRunStatusDto> StartRun()
    {
        if (!_coordinator.TryRequestStart())
        {
            return Conflict(new { message = "An assessment is already running." });
        }

        return Accepted(_coordinator.GetStatus());
    }

    /// <summary>
    /// Stops the run in flight. Answers 409 when there is nothing running.
    /// </summary>
    /// <remarks>
    /// Safe to press: every stage of a run commits as it goes, so a cancelled run keeps everything
    /// it had already assessed and the queue resumes from where it stopped. The status reports it
    /// as its own outcome rather than as a failure for that reason — see
    /// <c>VulnerabilityRunCoordinator.Cancelled</c>.
    ///
    /// Accepted rather than NoContent, and answered before the run has actually stopped: the run
    /// is on a background thread inside an HTTP call to NVD or OSV, so "stopping" is a state the
    /// screen shows (<c>Cancelling</c>) rather than something this request can wait for.
    /// </remarks>
    [HttpDelete("run")]
    [ProducesResponseType(typeof(VulnerabilityRunStatusDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<VulnerabilityRunStatusDto> CancelRun()
    {
        if (!_coordinator.TryCancel())
        {
            return Conflict(new { message = "No assessment is running." });
        }

        return Accepted(_coordinator.GetStatus());
    }
}

public record ConfirmCpeMappingRequest(string Vendor, string Product);

/// <summary>The selection a bulk Confirm or Clear applies to. Ids nothing matches are reported as
/// skipped rather than failing the batch — the screen's list may be a few seconds old.</summary>
public record BulkCpeMappingRequest(IReadOnlyList<Guid>? Ids);

public record SetCpeMappingNotApplicableRequest(string? Notes);
