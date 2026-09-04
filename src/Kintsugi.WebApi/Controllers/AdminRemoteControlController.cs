using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.RemoteControl;
using Kintsugi.Application.RemoteControl.Commands.EndRemoteControlSession;
using Kintsugi.Application.RemoteControl.Commands.MarkRemoteControlSessionStarted;
using Kintsugi.Application.RemoteControl.Commands.RequestRemoteControlSession;
using Kintsugi.Application.RemoteControl.Queries.GetRemoteControlSession;
using Kintsugi.WebApi.Filters;
using Kintsugi.WebApi.RemoteControl;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The browser's end of remote control: ask for a session, poll for the host user's answer, open
/// the stream, hang up.
/// </summary>
/// <remarks>
/// <para>
/// <c>[RequireAdminSession]</c> on the class, not per action, because nothing on this controller is
/// an agent route and the recurring failure this codebase has had twice is a route added later
/// inheriting no gate. It is also the only thing gating these routes: <c>/api/admin/...</c> is
/// outside nginx's client-certificate regex — deliberately, since a browser has no fleet
/// certificate — and <c>Program.cs</c> exempts the whole of <c>/api</c> from its own sign-in check.
/// </para>
/// <para>
/// The <c>/api/admin/</c> prefix is load-bearing for a second reason here: the agent's own socket
/// lives at <c>/api/remote-control</c>, which <em>is</em> inside that regex, so a browser-driven
/// route on that path would demand a certificate the browser has not got and fail as a 403 with
/// nothing in the C# to explain it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/remote-control")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminRemoteControlController : ControllerBase
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly ISender _sender;
    private readonly RemoteControlSessionBroker _broker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AdminRemoteControlController> _logger;

    public AdminRemoteControlController(
        ISender sender,
        RemoteControlSessionBroker broker,
        IServiceScopeFactory scopeFactory,
        ILogger<AdminRemoteControlController> logger)
    {
        _sender = sender;
        _broker = broker;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Asks the host's agent to put the consent dialog in front of whoever is sitting at it. Returns
    /// immediately with a session whose consent is <c>Pending</c> — or <c>AgentUnreachable</c>, if
    /// no agent was there to ask.
    /// </summary>
    [HttpPost("sessions")]
    [ProducesResponseType(typeof(RemoteControlSessionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RemoteControlSessionDto>> RequestSession(
        RemoteControlSessionRequest request,
        CancellationToken cancellationToken)
    {
        // Who is asking comes from the session cookie's own claims and never from the request body.
        // It goes into the audit record and onto the dialog the host user reads before deciding, so
        // a caller-supplied value here would be a caller-supplied answer to "who wants to watch your
        // screen".
        var session = await _sender.Send(
            new RequestRemoteControlSessionCommand(request.HostId, DescribeRequester()),
            cancellationToken);

        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, session);
    }

    /// <summary>What the remote-control screen polls while the dialog is up, and again to notice a
    /// session ending.</summary>
    [HttpGet("sessions/{id:guid}")]
    [ProducesResponseType(typeof(RemoteControlSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RemoteControlSessionDto>> GetSession(Guid id, CancellationToken cancellationToken)
    {
        var session = await _sender.Send(new GetRemoteControlSessionQuery(id), cancellationToken);
        return session is null ? NotFound() : Ok(session);
    }

    /// <summary>Hangs up: closes both sockets and stops the agent capturing.</summary>
    [HttpDelete("sessions/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> EndSession(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new EndRemoteControlSessionCommand(id, "the administrator disconnected"), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The viewer's socket. Everything on it is a contract between the browser and the agent — see
    /// <see cref="RemoteControlSessionBroker"/> on why this server relays it without looking.
    /// </summary>
    /// <remarks>
    /// A <c>location</c> for this prefix has to exist in <c>nginx/default.conf</c> carrying the
    /// <c>Upgrade</c>/<c>Connection</c> headers and a long <c>proxy_read_timeout</c>. The general
    /// <c>location /api</c> block has neither, so without its own block the handshake fails and the
    /// SPA fallback is not far below it.
    /// </remarks>
    [HttpGet("sessions/{id:guid}/stream")]
    [ProducesResponseType(StatusCodes.Status101SwitchingProtocols)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Stream(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return Problem(
                title: "A WebSocket request is required.",
                detail: "This route carries the remote control stream and is only reachable by upgrading to WebSocket.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = KeepAliveInterval
        });

        await _broker.RunViewerSocketAsync(id, socket, MarkStartedAsync, cancellationToken);

        return new EmptyResult();
    }

    /// <summary>
    /// The signed-in administrator's identity, or an explicit statement that there is none. On a
    /// server running with authentication deliberately disabled — which
    /// <see cref="RequireAdminSessionAttribute"/> allows, matching <c>Program.cs</c>'s own gate —
    /// there genuinely is nobody to name, and the audit record and the host user's dialog should
    /// both say exactly that rather than invent a name or leave it blank.
    /// </summary>
    private string DescribeRequester() =>
        User.Identity?.Name
        ?? User.FindFirst("email")?.Value
        ?? "an administrator (sign-in is disabled on this server)";

    /// <summary>See the matching note on <c>RemoteControlController</c>: a fresh scope, because this
    /// socket outlives any scoped DbContext, and no cancellation token, because this write must not
    /// be abandoned as the socket comes down.</summary>
    private async Task MarkStartedAsync(Guid sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new MarkRemoteControlSessionStartedCommand(sessionId), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the start of remote control session {SessionId}", sessionId);
        }
    }
}

/// <param name="HostId">The host to connect to. The only thing the caller gets to choose — see
/// <c>AdminRemoteControlController.RequestSession</c> on why the requester's identity is not part of
/// this body.</param>
public record RemoteControlSessionRequest(Guid HostId);
