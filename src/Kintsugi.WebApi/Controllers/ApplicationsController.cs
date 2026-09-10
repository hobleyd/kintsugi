using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Applications;
using Kintsugi.Application.Applications.Commands.RegisterApplications;
using Kintsugi.Application.Applications.Commands.ReportPatchResult;
using Kintsugi.Application.Applications.Queries.GetApplicationSummaries;
using Kintsugi.Application.PatchFailures.Commands.ReportPatchFailure;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ApplicationsController : ControllerBase
{
    private readonly ISender _sender;

    public ApplicationsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// Registers the full list of applications installed on a host, identified by
    /// serial number. Replaces any previously reported list for that host, so
    /// agents should call this with their complete current inventory each time.
    /// </summary>
    [HttpPost]
    [RequireAgentIdentity]
    [ProducesResponseType(typeof(RegisterApplicationsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegisterApplicationsResult>> Register(RegisterApplicationsCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));

    /// <summary>Lists installed applications by name, with a count of hosts reporting each one installed.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ApplicationSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ApplicationSummaryDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetApplicationSummariesQuery(), cancellationToken));

    /// <summary>
    /// Records that an agent successfully patched one already-installed application to a new
    /// version — called right after a patch cycle applies an upgrade, so the server's record of
    /// what's installed reflects it immediately rather than waiting on that host's next full
    /// inventory report. The previously registered version is what a *failed* attempt leaves
    /// in place, which is already correct; the failure itself is reported separately, to
    /// <see cref="ReportPatchFailure"/>.
    /// </summary>
    [HttpPost("/api/patch-results")]
    [RequireAgentIdentity]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ReportPatchResult(ReportPatchResultCommand command, CancellationToken cancellationToken)
    {
        await _sender.Send(command, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Records that an upgrade script (or package-manager command) ran on a host and failed —
    /// sent by whichever process actually ran it, carrying the host's own timestamp and the
    /// command's captured output. Surfaced on the admin UI's Failed Updates screen, where the
    /// script can be repaired by the AI or by hand and re-signed.
    /// </summary>
    /// <remarks>
    /// Route registered here beside <see cref="ReportPatchResult"/>, its success counterpart, and
    /// **added to nginx's exact-match agent regex in <c>nginx/default.conf</c>** — without that
    /// edit the route is reachable with no client certificate at all, and nothing in this file
    /// would say so.
    /// </remarks>
    [HttpPost("/api/patch-failures")]
    [RequireAgentIdentity]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReportPatchFailure(ReportPatchFailureCommand command, CancellationToken cancellationToken)
    {
        await _sender.Send(command, cancellationToken);
        return NoContent();
    }
}
