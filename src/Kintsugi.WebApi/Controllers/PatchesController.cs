using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Patches;
using Kintsugi.Application.Patches.Commands.CreatePatch;
using Kintsugi.Application.Patches.Queries.GetPatches;

namespace Kintsugi.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class PatchesController : ControllerBase
{
    private readonly ISender _sender;

    public PatchesController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Lists all known patches in the catalog.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PatchDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PatchDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetPatchesQuery(), cancellationToken));

    /// <summary>Adds a patch to the catalog.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(PatchDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PatchDto>> Create(CreatePatchCommand command, CancellationToken cancellationToken)
    {
        var patch = await _sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetAll), patch);
    }
}
