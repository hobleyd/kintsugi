using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.HostActivity;
using Kintsugi.Application.HostActivity.Commands.UpdateHostActivityRetention;
using Kintsugi.Application.HostActivity.Queries.GetHostActivity;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The per-host activity page: a host's timeline, pending updates and open failures, and how long
/// that history is kept.
/// </summary>
/// <remarks>
/// Under <c>api/admin</c>, so nginx's agent regex can never match it, and behind
/// <c>[RequireAdminSession]</c>: the timeline names the administrators who pressed things.
/// </remarks>
[ApiController]
[Route("api/admin/host-activity")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminHostActivityController : ControllerBase
{
    private readonly ISender _sender;

    public AdminHostActivityController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet("{hostId:guid}")]
    [ProducesResponseType(typeof(HostActivityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HostActivityDto>> Get(Guid hostId, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetHostActivityQuery(hostId), cancellationToken));

    [HttpPut("retention")]
    [ProducesResponseType(typeof(HostActivitySettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HostActivitySettingsDto>> SetRetention(
        UpdateHostActivityRetentionCommand command,
        CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));
}
