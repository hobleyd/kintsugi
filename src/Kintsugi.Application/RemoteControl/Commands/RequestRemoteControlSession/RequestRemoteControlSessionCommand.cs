using MediatR;

namespace Kintsugi.Application.RemoteControl.Commands.RequestRemoteControlSession;

/// <summary>
/// An administrator pressing Connect on the Hosts screen: opens a session and asks the host's agent
/// to put the consent dialog in front of whoever is sitting there.
/// </summary>
/// <param name="RequestedBy">
/// The signed-in administrator's identity, which the controller reads off the session cookie's
/// claims. Deliberately a command parameter rather than a field on the request body: this value
/// ends up in the audit record and on the dialog the host user reads, so it must not be anything a
/// caller could choose.
/// </param>
public record RequestRemoteControlSessionCommand(Guid HostId, string RequestedBy) : IRequest<RemoteControlSessionDto>;
