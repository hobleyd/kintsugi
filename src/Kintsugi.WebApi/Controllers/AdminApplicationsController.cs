using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Applications.Queries.GetApplicationOverview;
using Kintsugi.Application.ForcedPatchRuns;
using Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The Applications screen's own data, joined server-side.
/// </summary>
/// <remarks>
/// <para>
/// Under <c>/api/admin</c> rather than on <c>ApplicationsController</c> because that controller is
/// routed at <c>/api/applications</c>, which is one of the paths inside nginx's exact-match agent
/// regex — a route there requires a fleet client certificate that a browser does not have. Every
/// browser-driven route added from here on lives under this prefix for exactly that reason: it
/// cannot collide with that regex however the regex grows, and the collision is otherwise silent
/// (the call simply 403s with nothing in the C# to explain why).
/// </para>
/// <para>
/// The attribute is on the class, matching the precedent set by <see cref="AiSettingsController"/>
/// and the others: nothing here is an agent route, and the recurring failure is a route added
/// later inheriting no gate at all. Excluding a route from nginx's regex does not make it
/// browser-only, it makes it anonymous.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/applications")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminApplicationsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminApplicationsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ApplicationOverviewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApplicationOverviewDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetApplicationOverviewQuery(), cancellationToken));

    /// <summary>
    /// Tells the named hosts to run one application's upgrade script at their next opportunity,
    /// rather than waiting for their own patching schedule — the Applications screen's "Patch now"
    /// action, for an emergency an administrator is not willing to wait a patching interval for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The agent's end of the same feature is <c>GET /api/forced-patch-runs</c> on
    /// <see cref="ForcedPatchRunsController"/>, which is inside nginx's agent regex and therefore
    /// demands a fleet client certificate. This one is browser-driven, so it lives here, under the
    /// prefix that regex can never match, behind the class's <c>[RequireAdminSession]</c>.
    /// </para>
    /// <para>
    /// It raises an instruction and nothing more: no script crosses this route, and forcing a run
    /// cannot make an unsigned script runnable. See the remarks on <c>ForcedPatchRun</c>.
    /// </para>
    /// </remarks>
    [HttpPost("forced-patch-runs")]
    [ProducesResponseType(typeof(RequestForcedPatchRunsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RequestForcedPatchRunsResult>> ForcePatchRuns(
        RequestForcedPatchRunsCommand command,
        CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));
}
