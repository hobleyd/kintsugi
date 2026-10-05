using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Applications.Commands.RegisterApplications;
using Kintsugi.Application.Applications.Commands.ReportPatchResult;
using Kintsugi.Application.PatchFailures.Commands.ReportPatchFailure;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The agent-facing application routes. Every action here is identity-scoped by
/// <see cref="RequireAgentIdentityAttribute"/>, and that is now the whole of this controller.
/// </summary>
/// <remarks>
/// There used to be a <c>GET /api/applications</c> beside these, listing every application
/// installed anywhere in the fleet with the hosts reporting it. It sat inside nginx's
/// client-certificate regex, so it needed an agent certificate — and nothing more, because a read
/// with no body gives <see cref="RequireAgentIdentityAttribute"/> no serial number to compare the
/// certificate against. Any enrolled agent (or anyone holding one host's certificate, or anyone who
/// had enrolled a rogue one with a harvested token) could therefore read the whole fleet's
/// inventory, host names included. Nothing legitimate called it: the three agents only ever
/// <c>POST</c> this path, and the admin UI reads the Applications screen from
/// <c>/api/admin/applications</c>, which carries <c>[RequireAdminSession]</c>. So it was removed
/// rather than gated, the same way <c>report-version</c> and the agent-side <c>PUT
/// /api/patching-policy</c> were (see src/CLAUDE.md). If a fleet-wide read is ever wanted on an
/// agent route again, it needs a <c>serialNumber</c> to scope by and the attribute — not the regex
/// alone, which only proves the caller is <em>an</em> agent.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ApplicationsController : ControllerBase
{
    private readonly ISender _sender;

    public ApplicationsController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// Registers the full list of applications installed on a host, identified by
    /// serial number. Replaces any previously reported list for that host, so
    /// agents should call this with their complete current inventory each time.
    /// </summary>
    [HttpPost]
    [RequireAgentIdentity]
    [ProducesResponseType(typeof(RegisterApplicationsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegisterApplicationsResult>> Register(RegisterApplicationsCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));

    /// <summary>
    /// Records that an agent successfully patched one already-installed application to a new
    /// version — called right after a patch cycle applies an upgrade, so the server's record of
    /// what's installed reflects it immediately rather than waiting on that host's next full
    /// inventory report. The previously registered version is what a *failed* attempt leaves
    /// in place, which is already correct; the failure itself is reported separately, to
    /// <see cref="ReportPatchFailure"/>.
    /// </summary>
    [HttpPost("/api/patch-results")]
    [RequireAgentIdentity]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ReportPatchResult(ReportPatchResultCommand command, CancellationToken cancellationToken)
    {
        await _sender.Send(command, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Records that an upgrade script (or package-manager command) ran on a host and failed —
    /// sent by whichever process actually ran it, carrying the host's own timestamp and the
    /// command's captured output. Surfaced on the admin UI's Failed Updates screen, where the
    /// script can be repaired by the AI or by hand and re-signed.
    /// </summary>
    /// <remarks>
    /// Route registered here beside <see cref="ReportPatchResult"/>, its success counterpart, and
    /// **added to nginx's exact-match agent regex in <c>nginx/default.conf</c>** — without that
    /// edit the route is reachable with no client certificate at all, and nothing in this file
    /// would say so.
    /// </remarks>
    [HttpPost("/api/patch-failures")]
    [RequireAgentIdentity]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReportPatchFailure(ReportPatchFailureCommand command, CancellationToken cancellationToken)
    {
        await _sender.Send(command, cancellationToken);
        return NoContent();
    }
}
