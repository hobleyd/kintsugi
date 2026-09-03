using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// The live half of remote control: which hosts are currently reachable, and the state of the
/// sessions in flight. The durable half is <c>RemoteControlSession</c>, and the split matters —
/// everything behind this interface is in-memory and single-process, and is gone on restart.
/// </summary>
/// <remarks>
/// <para>
/// This is the narrow, Application-facing view of <c>RemoteControlSessionBroker</c>, which also
/// exposes the socket plumbing the controllers need. Registered twice, the same way the three
/// background coordinators are: the concrete type for the controllers, this interface for the
/// handlers.
/// </para>
/// <para>
/// Single-process is a real constraint, not an implementation detail waiting to be relaxed. A
/// session pairs two sockets that must land in the same process, so running two API replicas
/// behind a load balancer would break remote control specifically (and only remote control) unless
/// both sockets were routed to the same instance. Nothing in this deployment does that today —
/// docker-compose runs one api service — but it is the assumption to check first if that ever
/// changes.
/// </para>
/// </remarks>
public interface IRemoteControlSessionBroker
{
    /// <summary>
    /// Whether an agent for this host is currently holding a control socket — which is a stronger
    /// statement than the Hosts screen's own status. A host is "online" there if it checked in
    /// within the last interval, up to an hour ago; it is reachable here only if a per-user agent
    /// process has a socket open right now, which additionally means somebody is logged in.
    /// </summary>
    bool IsHostReachable(string serialNumber);

    /// <summary>
    /// Asks the agent to put the consent dialog on screen, and opens the in-memory session the two
    /// sockets will later be joined through.
    /// </summary>
    RemoteControlRequestOutcome TryRequestConsent(Guid sessionId, string serialNumber, string requestedBy, TimeSpan consentTimeout);

    /// <summary>
    /// The live consent state, which runs ahead of the stored row: the agent reports the answer over
    /// its control socket, and the row is written from there. Returns
    /// <see cref="RemoteControlConsent.Pending"/> for a session this process knows nothing about,
    /// so a poll that arrives after a restart reads as "still waiting" rather than as granted.
    /// </summary>
    RemoteControlConsent GetConsent(Guid sessionId);

    /// <summary>Whether the two sockets are currently joined and frames are flowing.</summary>
    bool IsActive(Guid sessionId);

    /// <summary>
    /// Tears the session down: cancels both sockets and tells the agent to stop capturing. Safe to
    /// call on a session that has already ended, or on one that never started.
    /// </summary>
    void EndSession(Guid sessionId, string reason);
}

/// <summary>What <see cref="IRemoteControlSessionBroker.TryRequestConsent"/> made of a request.</summary>
public enum RemoteControlRequestOutcome
{
    /// <summary>The agent was asked; the dialog is going up.</summary>
    Requested,

    /// <summary>No agent holds a control socket for this host, so nobody was asked anything.</summary>
    AgentUnreachable,

    /// <summary>
    /// Somebody is already connected to this host, or waiting on its consent dialog. One at a time,
    /// deliberately: two administrators driving the same mouse is not a feature, and a second
    /// dialog stacking on top of the first is how a user ends up approving the wrong request.
    /// </summary>
    AlreadyInSession
}
