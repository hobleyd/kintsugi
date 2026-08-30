using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Deployments;
using Kintsugi.Application.Deployments.Commands.ScheduleDeployment;
using Kintsugi.Application.Deployments.Queries.GetDeployments;

namespace Kintsugi.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class DeploymentsController : ControllerBase
{
    private readonly ISender _sender;

    public DeploymentsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Lists all scheduled and completed patch deployments.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PatchDeploymentDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PatchDeploymentDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetDeploymentsQuery(), cancellationToken));

    /// <summary>Schedules a patch deployment to a host.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(PatchDeploymentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PatchDeploymentDto>> Schedule(ScheduleDeploymentCommand command, CancellationToken cancellationToken)
    {
        var deployment = await _sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetAll), deployment);
    }
}
