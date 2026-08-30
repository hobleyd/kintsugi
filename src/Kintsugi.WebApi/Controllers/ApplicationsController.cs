using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Applications;
using Kintsugi.Application.Applications.Commands.RegisterApplications;
using Kintsugi.Application.Applications.Commands.ReportPatchResult;
using Kintsugi.Application.Applications.Queries.GetApplicationSummaries;
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
    /// inventory report. Nothing is reported here for a failed patch attempt; the previously
    /// registered version is simply left as-is.
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
}
