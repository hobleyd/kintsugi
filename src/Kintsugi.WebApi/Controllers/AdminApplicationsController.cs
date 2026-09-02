using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Applications.Queries.GetApplicationOverview;
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
}
