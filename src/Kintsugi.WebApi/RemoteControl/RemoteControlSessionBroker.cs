using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.WebApi.RemoteControl;

/// <summary>
/// The live half of remote control: the agents currently reachable, the sessions in flight, and the
/// relay that joins a viewer's socket to an agent's.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This relay does not understand the media protocol, and that is the design.</strong> Once
/// two sockets are joined, every frame and every keystroke is copied between them byte for byte,
/// message type and boundaries preserved, with nothing here parsing either direction. So the JPEG
/// tiling, the pointer coordinate space and the keycode mapping are a contract between
/// <c>clients/macos-agent/src/remote_control.rs</c> and <c>web/lib/presentation/remote_control/</c>
/// alone, and changing any of it needs no change to this file. What this server does own is the part
/// it is uniquely able to: proving who is on each end.
/// </para>
/// <para>
/// Both ends are authenticated by the mechanism that already exists for them. The agent's sockets
/// arrive on <c>/api/remote-control</c>, which is inside nginx's exact-match client-certificate
/// regex, and carry <c>[RequireAgentIdentity]</c> so the verified certificate CN must equal the
/// serial number in the query string. The viewer's arrives on <c>/api/admin/remote-control/...</c>,
/// outside that regex and carrying <c>[RequireAdminSession]</c>. Neither could be authenticated by
/// the other's mechanism, which is the whole reason this is a relay rather than a direct connection
/// — mutual TLS can only be verified by whatever terminates it, and nginx is that thing.
/// </para>
/// <para>
/// Everything here is in-memory and single-process. See <see cref="IRemoteControlSessionBroker"/> on
/// why that is a constraint to check rather than an accident.
/// </para>
/// </remarks>
public class RemoteControlSessionBroker : IRemoteControlSessionBroker
{
    /// <summary>Relay copy buffer. Comfortably larger than a control message and a fair fraction of
    /// a JPEG tile, so an ordinary frame is a couple of chunks rather than dozens.</summary>
    private const int RelayBufferBytes = 32 * 1024;

    private readonly ConcurrentDictionary<string, AgentControlSocket> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, RemoteControlRelaySession> _sessions = new();
    private readonly ILogger<RemoteControlSessionBroker> _logger;

    public RemoteControlSessionBroker(ILogger<RemoteControlSessionBroker> logger)
    {
        _logger = logger;
    }

    // ---------------------------------------------------------------------------------------------
    // IRemoteControlSessionBroker — the narrow view the Application handlers use.
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public bool IsHostReachable(string serialNumber) =>
        !string.IsNullOrWhiteSpace(serialNumber) && _agents.ContainsKey(serialNumber);

    /// <inheritdoc />
    public RemoteControlRequestOutcome TryRequestConsent(Guid sessionId, string serialNumber, string requestedBy, TimeSpan consentTimeout)
    {
        if (!_agents.TryGetValue(serialNumber, out var agent))
        {
            return RemoteControlRequestOutcome.AgentUnreachable;
        }

        PruneFinishedSessions();

        // One at a time per host. Checked against live sessions only: a session that ended is left
        // in the dictionary until the sweep in EndSession removes it, and must not block a retry.
        if (_sessions.Values.Any(s => s.MatchesHost(serialNumber) && !s.IsFinished))
        {
            return RemoteControlRequestOutcome.AlreadyInSession;
        }

        var session = new RemoteControlRelaySession(sessionId, serialNumber, requestedBy, consentTimeout);
        if (!_sessions.TryAdd(sessionId, session))
        {
            return RemoteControlRequestOutcome.AlreadyInSession;
        }

        var message = new RemoteControlProtocol.SessionRequested(sessionId, requestedBy, (int)consentTimeout.TotalSeconds);
        if (!agent.TrySend(JsonSerializer.Serialize(message, RemoteControlProtocol.Json)))
        {
            // The socket is in the dictionary but its send queue is closed or full — the agent is
            // going away. Report it as unreachable rather than leaving a session waiting on a
            // dialog that will never appear.
            _sessions.TryRemove(sessionId, out _);
            return RemoteControlRequestOutcome.AgentUnreachable;
        }

        _logger.LogInformation(
            "Remote control session {SessionId} requested for host {SerialNumber} by {RequestedBy}; awaiting the host user's consent",
            sessionId, serialNumber, requestedBy);

        return RemoteControlRequestOutcome.Requested;
    }

    /// <inheritdoc />
    public RemoteControlConsent GetConsent(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session.ResolveConsent() : RemoteControlConsent.Pending;

    /// <inheritdoc />
    public bool IsActive(Guid sessionId) => _sessions.TryGetValue(sessionId, out var session) && session.IsActive;

    /// <inheritdoc />
    public void EndSession(Guid sessionId, string reason)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        if (!session.Finish(reason))
        {
            return;
        }

        _logger.LogInformation("Remote control session {SessionId} ending: {Reason}", sessionId, reason);

        // Tells the agent to stop capturing even when no session socket was ever opened — a consent
        // granted to a viewer that then vanished has to be revoked over the control channel, or the
        // agent sits waiting for a peer that is not coming.
        if (_agents.TryGetValue(session.SerialNumber, out var agent))
        {
            agent.TrySend(JsonSerializer.Serialize(new RemoteControlProtocol.SessionEnded(sessionId, reason), RemoteControlProtocol.Json));
        }

        session.Cancel();
    }

    // ---------------------------------------------------------------------------------------------
    // The socket plumbing, used by the controllers only — hence on the concrete type rather than on
    // the interface, the same split the three background coordinators use.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Holds one agent's standing control socket for as long as it lives, and does not return until
    /// it closes. <paramref name="onConsent"/> and <paramref name="onSessionEnded"/> are how the
    /// caller persists what arrives; they are invoked one message at a time, and the caller is
    /// expected to open a fresh dependency-injection scope inside each — this socket outlives any
    /// scoped <c>DbContext</c> by hours.
    /// </summary>
    public async Task RunAgentControlSocketAsync(
        string serialNumber,
        WebSocket socket,
        Func<Guid, RemoteControlConsent, Task> onConsent,
        Func<Guid, string, Task> onSessionEnded,
        CancellationToken cancellationToken)
    {
        var control = new AgentControlSocket(serialNumber);

        // A reconnecting agent replaces its own previous socket. The old one is normally already
        // dead — the agent reconnects precisely because it lost the last one — and its half-open
        // remains would otherwise be handed session requests nobody would ever see.
        if (_agents.TryGetValue(serialNumber, out var previous))
        {
            _logger.LogInformation("Host {SerialNumber} reconnected its remote control socket; dropping the previous one", serialNumber);
            previous.Cancel();
        }

        _agents[serialNumber] = control;
        _logger.LogInformation("Host {SerialNumber} is now reachable for remote control", serialNumber);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, control.Cancellation.Token);

        var sending = PumpControlMessagesAsync(control, socket, linked.Token);
        var receiving = ReceiveControlMessagesAsync(serialNumber, socket, onConsent, onSessionEnded, linked.Token);

        try
        {
            // WhenAny does not rethrow, so a try/catch around it alone would never fire — both
            // halves are awaited individually below instead. Whichever finishes first, the other is
            // then cancelled and awaited too, so neither is left to fault unobserved.
            await Task.WhenAny(sending, receiving);
            control.Cancel();

            foreach (var half in new[] { sending, receiving })
            {
                try
                {
                    await half;
                }
                catch (OperationCanceledException)
                {
                    // Ordinary shutdown, either of this socket or of the process.
                }
                catch (WebSocketException ex)
                {
                    _logger.LogInformation(
                        "Remote control socket for host {SerialNumber} dropped: {Reason}", serialNumber, ex.Message);
                }
            }
        }
        finally
        {
            control.Cancel();

            // Only if it is still ours: a reconnect may already have replaced it, and removing the
            // new socket's entry would make a perfectly healthy agent look unreachable.
            if (_agents.TryGetValue(serialNumber, out var current) && ReferenceEquals(current, control))
            {
                _agents.TryRemove(serialNumber, out _);
            }

            // An agent that goes away takes its sessions with it, and the record must say so rather
            // than leaving a session that looks live forever.
            foreach (var session in _sessions.Values.Where(s => s.MatchesHost(serialNumber) && !s.IsFinished).ToList())
            {
                await SafelyReportEndedAsync(session.Id, "the host's agent disconnected", onSessionEnded);
            }

            _logger.LogInformation("Host {SerialNumber} is no longer reachable for remote control", serialNumber);
        }
    }

    /// <summary>
    /// Takes the agent's end of one session's media socket and does not return until the relay is
    /// over. Rejects anything that is not a granted, unfinished session belonging to this host.
    /// </summary>
    public Task RunAgentSessionSocketAsync(
        Guid sessionId,
        string serialNumber,
        WebSocket socket,
        Func<Guid, Task> onStarted,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || !session.MatchesHost(serialNumber))
        {
            // Includes the case where this API process restarted since consent was granted: no live
            // session, so no relay, whatever the stored row says.
            return CloseWithReasonAsync(socket, "no such remote control session for this host");
        }

        if (session.ResolveConsent() != RemoteControlConsent.Granted || session.IsFinished)
        {
            return CloseWithReasonAsync(socket, "this remote control session is not open");
        }

        if (!session.AttachAgentSocket(socket))
        {
            return CloseWithReasonAsync(socket, "this remote control session already has an agent connected");
        }

        session.OfferStartedCallback(onStarted);
        StartRelayIfReady(session);

        return session.WaitForAgentSideAsync(cancellationToken);
    }

    /// <summary>The browser's end of the same session. See
    /// <see cref="RunAgentSessionSocketAsync"/> — this is its mirror image.</summary>
    public Task RunViewerSocketAsync(Guid sessionId, WebSocket socket, Func<Guid, Task> onStarted, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return CloseWithReasonAsync(socket, "no such remote control session");
        }

        if (session.ResolveConsent() != RemoteControlConsent.Granted || session.IsFinished)
        {
            return CloseWithReasonAsync(socket, "this remote control session is not open");
        }

        // One viewer per session. There is no per-administrator authorization anywhere in this
        // application — any signed-in admin sees the whole fleet — so this is not an ownership
        // check; it is what stops a second tab silently joining a session whose audit record names
        // one person.
        if (!session.AttachViewerSocket(socket))
        {
            return CloseWithReasonAsync(socket, "this remote control session already has a viewer connected");
        }

        session.OfferStartedCallback(onStarted);
        StartRelayIfReady(session);

        return session.WaitForViewerSideAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------

    private async Task PumpControlMessagesAsync(AgentControlSocket control, WebSocket socket, CancellationToken cancellationToken)
    {
        // One writer, always. WebSocket.SendAsync forbids concurrent sends, and session requests
        // arrive on whichever request thread called TryRequestConsent — the queue is what keeps
        // those off this socket.
        await foreach (var message in control.Outbound.Reader.ReadAllAsync(cancellationToken))
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
    }

    private async Task ReceiveControlMessagesAsync(
        string serialNumber,
        WebSocket socket,
        Func<Guid, RemoteControlConsent, Task> onConsent,
        Func<Guid, string, Task> onSessionEnded,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            var text = await ReceiveTextMessageAsync(socket, buffer, cancellationToken);
            if (text is null)
            {
                return;
            }

            switch (RemoteControlProtocol.ReadType(text))
            {
                case RemoteControlProtocol.ConsentType:
                    await HandleConsentAsync(serialNumber, text, onConsent);
                    break;

                case RemoteControlProtocol.SessionEndedType:
                    var ended = JsonSerializer.Deserialize<RemoteControlProtocol.SessionEnded>(text, RemoteControlProtocol.Json);
                    if (ended is not null)
                    {
                        await SafelyReportEndedAsync(ended.SessionId, ended.Reason, onSessionEnded);
                    }
                    break;

                case RemoteControlProtocol.HelloType:
                    var hello = JsonSerializer.Deserialize<RemoteControlProtocol.Hello>(text, RemoteControlProtocol.Json);
                    _logger.LogInformation(
                        "Host {SerialNumber} available for remote control (agent {AgentVersion}, console user {ConsoleUser})",
                        serialNumber, hello?.AgentVersion ?? "unknown", hello?.ConsoleUser ?? "unknown");
                    break;

                default:
                    // Logged and skipped rather than fatal, so a newer agent sending something this
                    // server does not know about keeps working for everything it does know about.
                    _logger.LogDebug("Ignoring an unrecognised remote control message from host {SerialNumber}", serialNumber);
                    break;
            }
        }
    }

    private async Task HandleConsentAsync(string serialNumber, string text, Func<Guid, RemoteControlConsent, Task> onConsent)
    {
        var reported = JsonSerializer.Deserialize<RemoteControlProtocol.ConsentReported>(text, RemoteControlProtocol.Json);
        if (reported is null || !Enum.TryParse<RemoteControlConsent>(reported.Outcome, ignoreCase: true, out var outcome) ||
            outcome == RemoteControlConsent.Pending)
        {
            _logger.LogWarning("Host {SerialNumber} reported an unusable remote control consent outcome", serialNumber);
            return;
        }

        if (!_sessions.TryGetValue(reported.SessionId, out var session) || !session.MatchesHost(serialNumber))
        {
            // A host answering for a session that is not its own. Nothing is applied: the audit row
            // is keyed by session, and accepting this would let one enrolled agent decide another
            // host's consent.
            _logger.LogWarning(
                "Host {SerialNumber} reported consent for remote control session {SessionId}, which is not its own",
                serialNumber, reported.SessionId);
            return;
        }

        if (!session.LatchConsent(outcome))
        {
            return;
        }

        _logger.LogInformation(
            "Host {SerialNumber} answered remote control session {SessionId}: {Outcome}",
            serialNumber, reported.SessionId, outcome);

        await onConsent(reported.SessionId, outcome);

        if (outcome != RemoteControlConsent.Granted)
        {
            EndSession(reported.SessionId, outcome == RemoteControlConsent.TimedOut
                ? "nobody answered the consent dialog"
                : "the host user refused");
        }
    }

    private async Task SafelyReportEndedAsync(Guid sessionId, string reason, Func<Guid, string, Task> onSessionEnded)
    {
        EndSession(sessionId, reason);

        try
        {
            await onSessionEnded(sessionId, reason);
        }
        catch (Exception ex)
        {
            // The socket is already coming down; failing to write the audit row's end must not turn
            // that into an unhandled exception on the way out.
            _logger.LogError(ex, "Could not record the end of remote control session {SessionId}", sessionId);
        }
    }

    /// <summary>Reads one whole text message, or null once the peer closes. Binary messages on the
    /// control socket are a protocol error and are treated as a close.</summary>
    private static async Task<string?> ReceiveTextMessageAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType is WebSocketMessageType.Close or WebSocketMessageType.Binary)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    private void StartRelayIfReady(RemoteControlRelaySession session)
    {
        if (!session.TryClaimRelay())
        {
            return;
        }

        _ = RelayAsync(session);
    }

    private async Task RelayAsync(RemoteControlRelaySession session)
    {
        try
        {
            // Whichever side arrived first waits here for the other. Bounded, because consent
            // granted followed by the administrator closing the tab would otherwise leave the agent
            // capturing to nobody.
            var ready = await session.WaitForBothSocketsAsync(RemoteControlPairingTimeout);
            if (!ready)
            {
                EndSession(session.Id, "the other end never connected");
                return;
            }

            var agent = session.AgentSocket!;
            var viewer = session.ViewerSocket!;

            session.MarkActive();
            await session.InvokeStartedAsync(_logger);

            var token = session.Cancellation.Token;
            var toViewer = CopyAsync(agent, viewer, token);
            var toAgent = CopyAsync(viewer, agent, token);

            // Either direction finishing ends the session: a viewer that closed its tab has no more
            // use for frames, and an agent that stopped capturing has nothing to send.
            await Task.WhenAny(toViewer, toAgent);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            _logger.LogInformation("Remote control session {SessionId} relay dropped: {Reason}", session.Id, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote control session {SessionId} relay failed", session.Id);
        }
        finally
        {
            EndSession(session.Id, session.EndReason ?? "the connection closed");
            session.ReleaseBothSides();

            // Not removed here. A session's outcome has to stay readable for a while after it ends:
            // a consent dialog that timed out is known to this process and nowhere else, and the
            // browser's poll is what learns it (see GetRemoteControlSessionQueryHandler). Removing
            // the entry the moment it finished would make that answer indistinguishable from "still
            // waiting". PruneFinishedSessions clears them out later instead.
        }
    }

    /// <summary>
    /// Drops sessions that finished long enough ago that nothing is still polling them. Runs on the
    /// request path rather than on a timer, which is enough because the only thing that grows this
    /// dictionary is a new request: a process nobody is asking anything of is not accumulating
    /// anything either.
    /// </summary>
    private void PruneFinishedSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - FinishedSessionRetention;

        foreach (var (id, session) in _sessions)
        {
            if (session.FinishedAtUtc is { } finished && finished < cutoff)
            {
                _sessions.TryRemove(id, out _);
            }
        }
    }

    /// <summary>How long a finished session's live state is kept so a late poll can still read its
    /// outcome. Comfortably longer than the admin UI's poll interval.</summary>
    private static readonly TimeSpan FinishedSessionRetention = TimeSpan.FromMinutes(5);

    /// <summary>How long a granted session waits for both sockets. Mirrors
    /// <c>RemoteControlDefaults.PairingTimeout</c>; kept as a field here so the relay does not reach
    /// across into the Application layer for a constant.</summary>
    private static readonly TimeSpan RemoteControlPairingTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Copies messages one way, preserving message type and boundaries and interpreting nothing.
    /// This is the whole of what the server knows about the media protocol.
    /// </summary>
    private static async Task CopyAsync(WebSocket from, WebSocket to, CancellationToken cancellationToken)
    {
        var buffer = new byte[RelayBufferBytes];

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await from.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            // endOfMessage carried through per chunk rather than buffering the whole message: a
            // full-screen JPEG is a few hundred kilobytes, and holding one per direction per
            // session would be pure latency for no benefit.
            await to.SendAsync(
                new ArraySegment<byte>(buffer, 0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                cancellationToken);
        }
    }

    private static async Task CloseWithReasonAsync(WebSocket socket, string reason)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Nothing useful to do about a peer that has already gone.
        }
    }
}
