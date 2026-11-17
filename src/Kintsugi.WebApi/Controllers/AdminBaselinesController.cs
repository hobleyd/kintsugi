using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Authentication;
using Kintsugi.Application.Baselines;
using Kintsugi.Application.Baselines.Commands.AssignBaseline;
using Kintsugi.Application.Baselines.Commands.CreateBaseline;
using Kintsugi.Application.Baselines.Commands.CreateBaselineVersion;
using Kintsugi.Application.Baselines.Commands.SignBaselineVersion;
using Kintsugi.Application.Baselines.Queries.GetBaseline;
using Kintsugi.Application.Baselines.Queries.GetBaselineHost;
using Kintsugi.Application.Baselines.Queries.GetBaselines;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The Baselines screen: create a baseline or a new version, review and sign a version, assign it, and
/// read compliance. The agent's end is <see cref="BaselineController"/>.
/// </summary>
/// <remarks>
/// <c>[RequireAdminSession]</c> on the class, like every <c>/api/admin</c> controller: nothing here is an
/// agent route, and signing is the step that makes content reach every assigned device. Who created,
/// signed or assigned is taken from the session, never from the body.
/// </remarks>
[ApiController]
[Route("api/admin/baselines")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminBaselinesController : ControllerBase
{
    private readonly ISender _sender;

    public AdminBaselinesController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BaselineSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BaselineSummaryDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetBaselinesQuery(), cancellationToken));

    [HttpGet("{baselineId:guid}")]
    [ProducesResponseType(typeof(BaselineDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BaselineDetailDto>> Get(Guid baselineId, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetBaselineQuery(baselineId), cancellationToken));

    [HttpGet("{baselineId:guid}/hosts/{hostId:guid}")]
    [ProducesResponseType(typeof(BaselineHostDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BaselineHostDetailDto>> GetHost(Guid baselineId, Guid hostId, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetBaselineHostQuery(baselineId, hostId), cancellationToken));

    [HttpPost]
    [ProducesResponseType(typeof(CreatedBaselineVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreatedBaselineVersionDto>> Create(CreateBaselineRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new CreateBaselineCommand(request.Name, request.Items, AdminIdentity.SignerOf(User)), cancellationToken));

    [HttpPost("{baselineId:guid}/versions")]
    [ProducesResponseType(typeof(CreatedBaselineVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CreatedBaselineVersionDto>> CreateVersion(
        Guid baselineId, CreateBaselineVersionRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new CreateBaselineVersionCommand(baselineId, request.Items, AdminIdentity.SignerOf(User)), cancellationToken));

    /// <summary>Signs one version after review. <c>documentSha256</c> is the hash of the document text the
    /// reviewer was shown: 400 without it, 409 when the stored document no longer hashes to it.</summary>
    [HttpPost("versions/{versionId:guid}/sign")]
    [ProducesResponseType(typeof(BaselineVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BaselineVersionDto>> Sign(Guid versionId, SignBaselineVersionRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(
            new SignBaselineVersionCommand(versionId, request.DocumentSha256 ?? string.Empty, AdminIdentity.SignerOf(User)),
            cancellationToken));

    [HttpPut("{baselineId:guid}/assignment")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Assign(Guid baselineId, AssignBaselineRequest request, CancellationToken cancellationToken)
    {
        await _sender.Send(
            new AssignBaselineCommand(baselineId, request.AllWindowsHosts, request.HostNames ?? Array.Empty<string>(), AdminIdentity.SignerOf(User)),
            cancellationToken);
        return NoContent();
    }
}

/// <summary>The shape of <c>baselines/master-daily.json</c>, so that file can be pasted into the screen as-is.</summary>
public record CreateBaselineRequest(string Name, IReadOnlyList<BaselineItem> Items);

public record CreateBaselineVersionRequest(IReadOnlyList<BaselineItem> Items);

/// <param name="DocumentSha256">Nullable only so a missing value reaches the handler's own message.</param>
public record SignBaselineVersionRequest(string? DocumentSha256);

public record AssignBaselineRequest(bool AllWindowsHosts, IReadOnlyList<string>? HostNames);
