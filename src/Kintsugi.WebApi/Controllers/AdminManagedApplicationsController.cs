using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.ManagedApplications;
using Kintsugi.Application.ManagedApplications.Commands.AddManagedOwner;
using Kintsugi.Application.ManagedApplications.Commands.MarkApplicationsManaged;
using Kintsugi.Application.ManagedApplications.Commands.RemoveManagedOwner;
using Kintsugi.Application.ManagedApplications.Commands.UnmarkApplicationManaged;
using Kintsugi.Application.ManagedApplications.Queries.GetManagedApplications;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The applications another tool patches, and the list of tools that can own one. Every answer is the
/// whole of both, so the Managed Elsewhere page never has to guess what a change did.
/// </summary>
/// <remarks>
/// Admin-only, and under <c>/api/admin/</c> so nginx needs no entry for it. Marking an application
/// stops Kintsugi patching it on every host, which is why it is a signed-in administrator's decision
/// and not something a tool can do on its own — see <c>ManagedApplication</c>.
/// </remarks>
[ApiController]
[Route("api/admin/managed-applications")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminManagedApplicationsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminManagedApplicationsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ManagedApplicationsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ManagedApplicationsDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetManagedApplicationsQuery(), cancellationToken));

    /// <summary>Marks each of <see cref="MarkManagedRequest.Names"/> as patched by <see cref="MarkManagedRequest.Owner"/>.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ManagedApplicationsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ManagedApplicationsDto>> Mark(MarkManagedRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new MarkApplicationsManagedCommand(request.Names ?? Array.Empty<string>(), request.Owner ?? string.Empty), cancellationToken));

    /// <remarks>By query string, not path: an application's name can hold a slash, brackets or a plus,
    /// which route constraints and proxies treat as something else.</remarks>
    [HttpDelete]
    [ProducesResponseType(typeof(ManagedApplicationsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ManagedApplicationsDto>> Unmark([FromQuery] string name, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new UnmarkApplicationManagedCommand(name), cancellationToken));

    [HttpPost("owners")]
    [ProducesResponseType(typeof(ManagedApplicationsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ManagedApplicationsDto>> AddOwner(AddOwnerRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new AddManagedOwnerCommand(request.Name ?? string.Empty), cancellationToken));

    [HttpDelete("owners")]
    [ProducesResponseType(typeof(ManagedApplicationsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ManagedApplicationsDto>> RemoveOwner([FromQuery] string name, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new RemoveManagedOwnerCommand(name), cancellationToken));
}

/// <summary>The body of <c>POST /api/admin/managed-applications</c>.</summary>
public record MarkManagedRequest(IReadOnlyList<string>? Names, string? Owner);

/// <summary>The body of <c>POST /api/admin/managed-applications/owners</c>.</summary>
public record AddOwnerRequest(string? Name);
