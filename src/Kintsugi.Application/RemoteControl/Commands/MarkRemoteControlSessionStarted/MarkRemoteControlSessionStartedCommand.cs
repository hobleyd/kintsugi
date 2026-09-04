using MediatR;

namespace Kintsugi.Application.RemoteControl.Commands.MarkRemoteControlSessionStarted;

/// <summary>Records the moment the viewer's socket and the agent's were joined and frames began
/// flowing — the point at which somebody was actually looking at somebody else's screen.</summary>
public record MarkRemoteControlSessionStartedCommand(Guid SessionId) : IRequest<Unit>;
