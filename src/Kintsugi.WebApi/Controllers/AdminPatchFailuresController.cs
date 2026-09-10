using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Application.PatchFailures.Commands.DismissPatchFailure;
using Kintsugi.Application.PatchFailures.Queries.GetPatchFailures;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The Failed Updates screen's data: every upgrade an agent tried to apply and could not, with the
/// host, the date and the failing script's own output.
/// </summary>
/// <remarks>
/// <para>
/// Under <c>/api/admin</c> for the reason <see cref="AdminApplicationsController"/> gives at length —
/// a browser-driven route must not sit on a path inside nginx's exact-match agent regex. The
/// agent-facing half of this feature is <c>POST /api/patch-failures</c> on
/// <see cref="ApplicationsController"/>, which *is* in that regex and carries
/// <see cref="RequireAgentIdentityAttribute"/>.
/// </para>
/// <para>
/// The attribute is on the class, matching the precedent the other admin controllers set: nothing
/// here is an agent route, and the recurring failure is a route added later inheriting no gate.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/patch-failures")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminPatchFailuresController : ControllerBase
{
    private readonly ISender _sender;

    public AdminPatchFailuresController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Every reported patch failure, outstanding ones first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PatchFailureDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PatchFailureDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetPatchFailuresQuery(), cancellationToken));

    /// <summary>
    /// Clears one failure off the screen by hand. For failures that cannot recur — everything else
    /// closes itself when the host next reports that application patched successfully.
    /// </summary>
    [HttpPost("{id:guid}/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DismissPatchFailureCommand(id), cancellationToken);
        return NoContent();
    }
}
