using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.RemoteControl.Commands.RecordRemoteControlConsent;

/// <summary>
/// The answer the host user gave, arriving over the agent's own control socket. Written from the
/// socket handler rather than from the browser's poll, so the record is made whether or not the
/// administrator is still watching the page.
/// </summary>
public record RecordRemoteControlConsentCommand(Guid SessionId, RemoteControlConsent Outcome) : IRequest<Unit>;
