using MediatR;

namespace Kintsugi.Application.Hosts.Commands.RequestHostRemoval;

/// <summary>
/// An admin asking, from the hosts table, that a host be removed: hides it from the hosts list
/// immediately and, on its next check-in, tells the agent to uninstall itself completely from the
/// host machine. The record itself is only permanently deleted once that agent confirms it did so
/// — see ConfirmHostRemovalCommand.
/// </summary>
public record RequestHostRemovalCommand(Guid Id) : IRequest<Unit>;
