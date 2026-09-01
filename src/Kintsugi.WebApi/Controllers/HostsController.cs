using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Hosts;
using Kintsugi.Application.Hosts.Commands.ConfirmHostRemoval;
using Kintsugi.Application.Hosts.Commands.CreateHost;
using Kintsugi.Application.Hosts.Commands.EnrollAgent;
using Kintsugi.Application.Hosts.Commands.ReportOperatingSystemPatched;
using Kintsugi.Application.Hosts.Commands.RequestHostRemoval;
using Kintsugi.Application.Hosts.Queries.GetHostById;
using Kintsugi.Application.Hosts.Queries.GetHosts;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class HostsController : ControllerBase
{
    private readonly ISender _sender;

    public HostsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Lists all managed hosts.</summary>
    // Admin-gated: the whole fleet inventory.
    [HttpGet]
    [RequireAdminSession]
    [ProducesResponseType(typeof(IReadOnlyList<HostDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<HostDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetHostsQuery(), cancellationToken));

    /// <summary>Gets a single host by id.</summary>
    // Admin-gated: one host's full detail.
    [HttpGet("{id:guid}")]
    [RequireAdminSession]
    [ProducesResponseType(typeof(HostDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HostDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var host = await _sender.Send(new GetHostByIdQuery(id), cancellationToken);
        return host is null ? NotFound() : Ok(host);
    }

    /// <summary>
    /// Registers a host by hostname and serial number, or refreshes its last-seen status
    /// and hostname if a host with that serial number is already registered. Safe to call
    /// repeatedly (e.g. on every boot and on a recurring schedule). The response's
    /// <c>suggestedCheckInMinute</c> is only ever set when this host's reported
    /// <c>checkInMinute</c> is carrying more load than others right now — see
    /// <see cref="Application.Common.Interfaces.ICheckInLoadBalancer"/> — and the agent is
    /// expected to switch to it (see clients/macos-agent/src/checkin_schedule.rs).
    /// </summary>
    [HttpPost("/api/host")]
    [RequireAgentIdentity]
    [ProducesResponseType(typeof(CreateHostResult), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateHostResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CreateHostResult>> Create(CreateHostCommand command, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(command, cancellationToken);
        return result.WasCreated
            ? CreatedAtAction(nameof(GetById), new { id = result.Host.Id }, result)
            : Ok(result);
    }

    /// <summary>
    /// A brand-new agent's one-time bootstrap: presents the shared enrollment token plus a CSR it
    /// generated locally (its private key never leaves the agent), and gets back a client
    /// certificate — signed by this fleet's own CA — bound to its serial number. nginx requires
    /// and verifies that certificate on every subsequent agent-only request (see
    /// nginx/default.conf); deliberately not behind <see cref="RequireAgentIdentityAttribute"/>
    /// itself, since an unenrolled agent has no certificate yet to be checked against.
    /// </summary>
    [HttpPost("/api/host/enroll")]
    [ProducesResponseType(typeof(EnrollAgentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<EnrollAgentResult>> Enroll(EnrollAgentCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));

    /// <summary>
    /// Records that an agent successfully installed a pending macOS update — called right after
    /// the root daemon's install-via-daemon handoff reports success, so this host's pending-update
    /// flag and target version clear immediately rather than waiting on its next check-in to
    /// re-derive them from a fresh <c>softwareupdate -l</c> run.
    /// </summary>
    [HttpPost("/api/os-patch-results")]
    [RequireAgentIdentity]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReportOperatingSystemPatched(ReportOperatingSystemPatchedCommand command, CancellationToken cancellationToken)
    {
        await _sender.Send(command, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Removes a host: hides it from the hosts list immediately, and flags it so its next
    /// check-in's response (see <see cref="Create"/> and <c>HostDto.RemovalRequested</c>) tells the
    /// agent to uninstall itself completely from the host machine. The record itself stays
    /// soft-deleted, not gone, until that agent confirms it actually did so — see
    /// <see cref="ConfirmRemoval"/>.
    /// </summary>
    // Admin-gated, and the most important one on this controller: destructive, browser-initiated,
    // and "hosts" being plural means it never matched nginx's agent regex, so it was reachable by
    // anyone who could reach the server.
    [HttpDelete("{id:guid}")]
    [RequireAdminSession]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RequestRemoval(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new RequestHostRemovalCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The agent's final word after a requested removal: it has finished uninstalling itself
    /// completely from the host machine. This is what actually deletes the host record — see
    /// <see cref="RequestRemoval"/>.
    /// </summary>
    [HttpPost("/api/host-removed")]
    [RequireAgentIdentity]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConfirmRemoval(ConfirmHostRemovalCommand command, CancellationToken cancellationToken)
    {
        await _sender.Send(command, cancellationToken);
        return NoContent();
    }
}
