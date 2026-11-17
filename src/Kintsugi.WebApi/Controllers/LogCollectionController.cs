using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.LogCollections;
using Kintsugi.Application.LogCollections.Commands;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The agent's end of "collect this device's log": it asks at its check-in, reads its own operating system's log,
/// and sends the result back. The browser's end is <see cref="AdminDeviceReportController"/>.
/// </summary>
/// <remarks>
/// Three single-segment routes, **all in nginx's exact-match agent regex** in <c>nginx/default.conf</c>: without that
/// edit they are reachable with no client certificate at all. <c>[RequireAgentIdentity]</c> ties each call to the
/// host the certificate belongs to, and the handlers refuse a collection that is another host's.
/// </remarks>
[ApiController]
[Produces("application/json")]
public class LogCollectionController : ControllerBase
{
    private readonly ISender _sender;

    public LogCollectionController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Hands the host the log request waiting for it, if any, and consumes it.</summary>
    [HttpGet("/api/log-collection-requests")]
    [RequireAgentIdentity]
    [ProducesResponseType(typeof(IReadOnlyList<LogCollectionRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<LogCollectionRequestDto>>> Claim([FromQuery] string serialNumber, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new ClaimLogCollectionRequestsCommand(serialNumber), cancellationToken));

    /// <summary>The log itself: the body is a gzip of JSON, not a JSON body, so it travels as raw bytes with the
    /// identifiers in the query.</summary>
    [HttpPost("/api/log-collection-results")]
    [RequireAgentIdentity]
    [RequestSizeLimit(LogPayload.MaxCompressedBytes + 4096)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Result([FromQuery] string serialNumber, [FromQuery] Guid collectionId, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await Request.Body.CopyToAsync(body, cancellationToken);
        await _sender.Send(new CompleteLogCollectionCommand(serialNumber, collectionId, body.ToArray()), cancellationToken);
        return NoContent();
    }

    [HttpPost("/api/log-collection-failures")]
    [RequireAgentIdentity]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Failure(FailLogCollectionCommand command, CancellationToken cancellationToken)
    {
        await _sender.Send(command, cancellationToken);
        return NoContent();
    }
}
