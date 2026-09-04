using MediatR;

namespace Kintsugi.Application.RemoteControl.Queries.GetRemoteControlSession;

/// <summary>
/// What the remote-control screen polls while it waits for the host user to answer, and again to
/// notice a session ending. Returns null for an unknown id.
/// </summary>
public record GetRemoteControlSessionQuery(Guid Id) : IRequest<RemoteControlSessionDto?>;
