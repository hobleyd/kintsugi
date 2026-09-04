using System.Net.WebSockets;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.RemoteControl.Commands.EndRemoteControlSession;
using Kintsugi.Application.RemoteControl.Commands.MarkRemoteControlSessionStarted;
using Kintsugi.Application.RemoteControl.Commands.RecordRemoteControlConsent;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.Filters;
using Kintsugi.WebApi.RemoteControl;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The agent's end of remote control: one route, two jobs, decided by whether a session id is
/// present.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One path, because nginx's agent regex matches a single segment.</strong> That regex —
/// <c>^/api/(host|applications|...|remote-control)$</c> — is what requires a fleet client
/// certificate, and it is an exact match on one path segment. Both sockets therefore live at
/// <c>/api/remote-control</c> and are distinguished by query string rather than by path, which also
/// means <see cref="RequireAgentIdentityAttribute"/> works unchanged: it falls back to an action
/// argument named <c>serialNumber</c>, so the verified certificate CN is compared against the
/// serial number in the query exactly as it is compared against the one in a request body
/// elsewhere. A WebSocket handshake has no body for it to read.
/// </para>
/// <para>
/// Adding a second path here would need that regex rewritten to something other than an exact
/// match, which is the change CLAUDE.md warns hardest about — get it wrong and every agent route
/// is served with no certificate at all.
/// </para>
/// </remarks>
[ApiController]
public class RemoteControlController : ControllerBase
{
    /// <summary>
    /// How often the server sends a WebSocket ping on an otherwise idle socket. The control socket
    /// is silent for hours at a time, and something has to keep it from looking dead: nginx's
    /// <c>proxy_read_timeout</c> on this location, and any stateful firewall between the host and
    /// the server, both measure idleness in bytes on the wire.
    /// </summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly RemoteControlSessionBroker _broker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RemoteControlController> _logger;

    public RemoteControlController(
        RemoteControlSessionBroker broker,
        IServiceScopeFactory scopeFactory,
        ILogger<RemoteControlController> logger)
    {
        _broker = broker;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// With no <paramref name="sessionId"/>: the agent's standing control socket, over which it is
    /// asked for consent and reports the answer. With one: that session's media socket, whose bytes
    /// this server relays to the browser without interpreting them.
    /// </summary>
    [HttpGet("/api/remote-control")]
    [RequireAgentIdentity]
    [ProducesResponseType(StatusCodes.Status101SwitchingProtocols)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Connect(
        [FromQuery] string serialNumber,
        [FromQuery] Guid? sessionId,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return Problem(
                title: "A WebSocket request is required.",
                detail: "This route is the agent's remote control channel and is only reachable by upgrading to WebSocket.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = KeepAliveInterval
        });

        if (sessionId is { } id)
        {
            await _broker.RunAgentSessionSocketAsync(id, serialNumber, socket, MarkStartedAsync, cancellationToken);
        }
        else
        {
            await _broker.RunAgentControlSocketAsync(serialNumber, socket, RecordConsentAsync, RecordEndedAsync, cancellationToken);
        }

        // The socket is finished by the time we get here; returning anything with a body would be
        // written to a connection that is no longer HTTP.
        return new EmptyResult();
    }

    // ---------------------------------------------------------------------------------------------
    // A fresh scope per message, and CancellationToken.None on purpose.
    //
    // These callbacks are invoked from inside a socket that lives for hours, so they cannot close
    // over anything scoped to the request that accepted it — a DbContext held open for the life of
    // an agent's connection would be one per logged-in host in the fleet. And they are called as
    // the socket comes down as well as while it is up: passing the request's cancellation token
    // would abandon exactly the writes that record why a session ended.
    // ---------------------------------------------------------------------------------------------

    private Task RecordConsentAsync(Guid sessionId, RemoteControlConsent outcome) =>
        SendAsync(new RecordRemoteControlConsentCommand(sessionId, outcome), sessionId);

    private Task RecordEndedAsync(Guid sessionId, string reason) =>
        SendAsync(new EndRemoteControlSessionCommand(sessionId, reason), sessionId);

    private Task MarkStartedAsync(Guid sessionId) =>
        SendAsync(new MarkRemoteControlSessionStartedCommand(sessionId), sessionId);

    private async Task SendAsync(IRequest<Unit> command, Guid sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(command, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Never thrown back into the socket loop: an audit write failing must not take down a
            // session that is otherwise working, and must not stop the loop noticing the socket
            // closing. Reported instead, which is the only useful thing left to do about it.
            _logger.LogError(ex, "Could not record {Command} for remote control session {SessionId}", command.GetType().Name, sessionId);
        }
    }
}
