using MediatR;

namespace Kintsugi.Application.RemoteControl.Commands.EndRemoteControlSession;

/// <summary>
/// Ends a session and closes out its record. One command for every way a session finishes — the
/// administrator pressing Disconnect, either socket dropping, the host user revoking from the menu
/// bar — because the broker's teardown is idempotent and the record keeps the first reason it was
/// given, so the caller that noticed first is the one whose explanation survives.
/// </summary>
public record EndRemoteControlSessionCommand(Guid SessionId, string Reason) : IRequest<Unit>;
