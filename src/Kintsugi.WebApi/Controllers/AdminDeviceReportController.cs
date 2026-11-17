using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.DeviceReport;
using Kintsugi.Application.LogCollections;
using Kintsugi.Application.LogCollections.Commands;
using Kintsugi.Application.LogCollections.Queries;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// One device's report: what happened to it, whether Kintsugi was involved, what it could be, and the operating
/// system log an administrator asked its agent to collect. One device at a time, never a group.
/// </summary>
/// <remarks>
/// Under <c>api/admin</c> (outside nginx's agent regex) and behind <c>[RequireAdminSession]</c>: collected logs can
/// name people and files, so only administrators read them, and each request, download and deletion is recorded on
/// the device's timeline with the administrator's name.
/// </remarks>
[ApiController]
[Route("api/admin/device-report")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminDeviceReportController : ControllerBase
{
    private readonly ISender _sender;

    public AdminDeviceReportController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Devices whose name or serial number contains the text, at most <c>take</c> (up to 20).</summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(IReadOnlyList<DeviceSearchResultDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DeviceSearchResultDto>>> Search([FromQuery] string q = "", [FromQuery] int take = 10, CancellationToken cancellationToken = default) =>
        Ok(await _sender.Send(new SearchDevicesQuery(q, take), cancellationToken));

    /// <param name="days">7 or 30.</param>
    [HttpGet("{hostId:guid}")]
    [ProducesResponseType(typeof(DeviceReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceReportDto>> Get(Guid hostId, [FromQuery] int days = 7, CancellationToken cancellationToken = default) =>
        Ok(await _sender.Send(new GetDeviceReportQuery(hostId, days), cancellationToken));

    public record RequestLogsBody(int PeriodDays, string Detail);

    /// <summary>The newest collection for the device, without its content: what the page polls while it waits.</summary>
    [HttpGet("{hostId:guid}/logs")]
    [ProducesResponseType(typeof(LogCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<LogCollectionDto>> Latest(Guid hostId, CancellationToken cancellationToken)
    {
        var latest = await _sender.Send(new GetLatestLogCollectionQuery(hostId), cancellationToken);
        return latest is null ? NoContent() : Ok(latest);
    }

    /// <summary>Asks the device's agent to collect its log at its next check-in. The administrator is the
    /// signed-in one, set here and never read from the body.</summary>
    [HttpPost("{hostId:guid}/logs")]
    [ProducesResponseType(typeof(LogCollectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LogCollectionDto>> Request(Guid hostId, [FromBody] RequestLogsBody body, CancellationToken cancellationToken)
    {
        var created = await _sender.Send(
            new RequestLogCollectionCommand(hostId, body.PeriodDays, body.Detail, AdminRemoteControlController.DescribeRequester(User)),
            cancellationToken);
        return CreatedAtAction(nameof(Latest), new { hostId }, created);
    }

    /// <summary>Deletes a collected log now, or cancels a request nobody has picked up.</summary>
    [HttpDelete("{hostId:guid}/logs/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid hostId, Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DeleteLogCollectionCommand(hostId, id, AdminRemoteControlController.DescribeRequester(User)), cancellationToken);
        return NoContent();
    }

    /// <param name="severity">"all", "errors" or "warnings".</param>
    /// <param name="aroundUtc">With <c>aroundMinutes</c>: only entries that close to this moment.</param>
    [HttpGet("{hostId:guid}/logs/{id:guid}/entries")]
    [ProducesResponseType(typeof(LogEntriesPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LogEntriesPageDto>> Entries(
        Guid hostId,
        Guid id,
        [FromQuery] string? severity = null,
        [FromQuery] string? search = null,
        [FromQuery] DateTimeOffset? aroundUtc = null,
        [FromQuery] int aroundMinutes = 10,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 150,
        CancellationToken cancellationToken = default) =>
        Ok(await _sender.Send(new GetLogEntriesQuery(hostId, id, severity, search, aroundUtc, aroundMinutes, skip, take), cancellationToken));

    [HttpGet("{hostId:guid}/logs/{id:guid}/download")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid hostId, Guid id, CancellationToken cancellationToken)
    {
        var file = await _sender.Send(new GetLogDownloadQuery(hostId, id, AdminRemoteControlController.DescribeRequester(User)), cancellationToken);
        return File(file.Zip, "application/zip", file.FileName);
    }
}
