using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.ForcedPatchRuns;
using Kintsugi.Application.ForcedPatchRuns.Commands.ClaimForcedPatchRuns;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The agent's end of "patch this now": one route, polled, that hands a host the forced patch runs
/// raised against it since it last looked.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Its own controller, and its own single-segment path, because of nginx.</strong> The
/// exact-match agent regex in <c>nginx/default.conf</c> matches one path segment, so an agent route
/// cannot be a sub-path of an existing controller — and <c>forced-patch-runs</c> had to be added to
/// that regex in the same change that added this file. Without the regex edit the route is
/// reachable with **no client certificate at all**, and nothing in this C# would say so. It is the
/// route that tells a machine to run a script as root; treat the two edits as one.
/// </para>
/// <para>
/// The browser's end is <c>POST /api/admin/applications/forced-patch-runs</c> on
/// <see cref="AdminApplicationsController"/> — deliberately a different path, because a browser has
/// no agent certificate and anything under this one demands it.
/// </para>
/// </remarks>
[ApiController]
[Produces("application/json")]
public class ForcedPatchRunsController : ControllerBase
{
    private readonly ISender _sender;

    public ForcedPatchRunsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// Lists — and, in the same call, consumes — every forced patch run waiting for this host. See
    /// <see cref="ClaimForcedPatchRunsCommand"/> for why a GET is the right verb for something that
    /// writes, and <c>ForcedPatchRun</c> for why collecting a row has to consume it.
    /// </summary>
    [HttpGet("/api/forced-patch-runs")]
    [RequireAgentIdentity]
    [ProducesResponseType(typeof(IReadOnlyList<ForcedPatchRunDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ForcedPatchRunDto>>> Claim(
        [FromQuery] string serialNumber,
        CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new ClaimForcedPatchRunsCommand(serialNumber), cancellationToken));
}
