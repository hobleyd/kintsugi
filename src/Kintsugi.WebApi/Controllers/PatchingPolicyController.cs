using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.PatchingPolicy;
using Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;
using Kintsugi.Application.PatchingPolicy.Queries.GetPatchingPolicySettings;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The fleet-wide patching policy — how often patching runs, and how long/how many times a
/// required restart or reboot can be deferred. Read by the kintsugi-agent (in a future step) to
/// decide when to check for and apply patches, and how to handle a disruptive one, rather than the
/// server pushing patches on a schedule itself.
/// </summary>
[ApiController]
[Route("api/patching-policy")]
[Produces("application/json")]
public class PatchingPolicyController : ControllerBase
{
    private readonly ISender _sender;

    public PatchingPolicyController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Gets the current patching policy — sensible defaults if none has been saved yet.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PatchingPolicySettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PatchingPolicySettingsDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetPatchingPolicySettingsQuery(), cancellationToken));

    /// <summary>Creates or updates the patching policy.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(PatchingPolicySettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PatchingPolicySettingsDto>> Update(UpdatePatchingPolicySettingsCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));
}
