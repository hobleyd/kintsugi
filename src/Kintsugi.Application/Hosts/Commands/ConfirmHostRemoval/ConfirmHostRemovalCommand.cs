using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Hosts.Commands.ConfirmHostRemoval;

/// <summary>
/// The agent's final word after a requested removal (see RequestHostRemovalCommand): it has
/// finished uninstalling itself completely from the host machine. Permanently deletes the host
/// record — until this arrives, the host stays soft-deleted rather than gone, since there's
/// otherwise no proof the agent ever actually removed itself.
/// </summary>
public record ConfirmHostRemovalCommand(string SerialNumber) : IRequest<Unit>, IAgentScopedRequest;
