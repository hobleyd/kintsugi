using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Autonomy;
using Kintsugi.Application.Autonomy.Commands.RequestAutonomyPatchNow;
using Kintsugi.Application.Autonomy.Queries.GetAutonomy;
using Kintsugi.Application.Autonomy.Queries.GetAutonomyCauseDevices;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The Autonomy page: who started each patch, what is holding patching up and how much it costs, the
/// key figures against their bands, and Patch now for a whole cause. Built only from what the server
/// records, so it covers macOS, Linux and Windows hosts alike.
/// </summary>
/// <remarks>
/// Under <c>api/admin</c> (outside nginx's agent regex) and behind <c>[RequireAdminSession]</c>: it names
/// the administrators who pressed Patch now, and Patch now acts on managed machines.
/// </remarks>
[ApiController]
[Route("api/admin/autonomy")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminAutonomyController : ControllerBase
{
    private readonly ISender _sender;

    public AdminAutonomyController(ISender sender)
    {
        _sender = sender;
    }

    /// <param name="period">"7d", "30d" or "month".</param>
    /// <param name="month">"yyyy-MM", with <c>month</c>.</param>
    [HttpGet]
    [ProducesResponseType(typeof(AutonomyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AutonomyDto>> Get(
        [FromQuery] string period = "30d",
        [FromQuery] string? month = null,
        CancellationToken cancellationToken = default) =>
        Ok(await _sender.Send(new GetAutonomyQuery(period, month), cancellationToken));

    /// <summary>A page of the devices held up by one cause, optionally filtered by name.</summary>
    [HttpGet("causes/{key}/devices")]
    [ProducesResponseType(typeof(AutonomyDevicesPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AutonomyDevicesPageDto>> Devices(
        string key,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default) =>
        Ok(await _sender.Send(new GetAutonomyCauseDevicesQuery(key, skip, take, search), cancellationToken));

    public record PatchNowRequest(int ExpectedConnected, bool IncludeOffline);

    /// <summary>Patch now for every Windows device in the cause. The administrator is the signed-in one,
    /// set here and never read from the body.</summary>
    [HttpPost("causes/{key}/patch-now")]
    [ProducesResponseType(typeof(AutonomyPatchNowResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AutonomyPatchNowResult>> PatchNow(
        string key,
        PatchNowRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _sender.Send(
            new RequestAutonomyPatchNowCommand(key, request.ExpectedConnected, request.IncludeOffline, AdminRemoteControlController.DescribeRequester(User)),
            cancellationToken));
}
