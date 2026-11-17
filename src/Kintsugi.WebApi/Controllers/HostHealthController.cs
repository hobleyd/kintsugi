using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.DeviceHealth.Commands.ReportHostHealthEvents;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The agent's report of what its operating system logged about the device shutting down by itself and
/// about hardware errors. The browser's end is the Autonomy page.
/// </summary>
/// <remarks>
/// Route registered here and <b>added to nginx's exact-match agent regex in <c>nginx/default.conf</c></b>:
/// without that edit the route is reachable with no client certificate at all, and nothing in this file
/// would say so. <c>[RequireAgentIdentity]</c> then ties the report to the host the certificate belongs to.
/// </remarks>
[ApiController]
[Produces("application/json")]
public class HostHealthController : ControllerBase
{
    private readonly ISender _sender;

    public HostHealthController(ISender sender)
    {
        _sender = sender;
    }

    [HttpPost("/api/host-health-events")]
    [RequireAgentIdentity]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Report(ReportHostHealthEventsCommand command, CancellationToken cancellationToken)
    {
        await _sender.Send(command, cancellationToken);
        return NoContent();
    }
}
