using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Baselines;
using Kintsugi.Application.Baselines.Commands.ReportBaselineResults;
using Kintsugi.Application.Baselines.Queries.GetAgentBaselines;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The agent's two baseline routes: fetch the signed baselines assigned to this host, and report what
/// was read. Audit only in this step — nothing here, and nothing the agent does with it, writes to a
/// device. The browser's end is <see cref="AdminBaselinesController"/>.
/// </summary>
/// <remarks>
/// <strong>Both paths are in nginx's exact-match agent regex in <c>nginx/default.conf</c></strong>, and
/// each is its own single segment because that regex matches one. Without the regex edit these routes
/// are reachable with no client certificate at all, and nothing in this file would say so;
/// <c>[RequireAgentIdentity]</c> then ties each request to the host its certificate belongs to.
/// <c>NginxAgentRouteTests</c> fails if either path drops out of the regex.
/// </remarks>
[ApiController]
[Produces("application/json")]
public class BaselineController : ControllerBase
{
    private readonly ISender _sender;

    public BaselineController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet("/api/baseline")]
    [RequireAgentIdentity]
    [ProducesResponseType(typeof(AgentBaselinesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AgentBaselinesDto>> Get([FromQuery] string serialNumber, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetAgentBaselinesQuery(serialNumber), cancellationToken));

    [HttpPost("/api/baseline-results")]
    [RequireAgentIdentity]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Report(ReportBaselineResultsCommand command, CancellationToken cancellationToken)
    {
        await _sender.Send(command, cancellationToken);
        return NoContent();
    }
}
