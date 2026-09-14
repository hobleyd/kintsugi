using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.ForcedPatchRuns.Commands.ClaimForcedPatchRuns;

/// <summary>
/// Hands one host every forced patch run still waiting for it, and stamps them collected in the
/// same breath. Called by the agent on its own poll tick.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A command rather than a query, even though the route is a GET.</strong> Reading this is
/// what delivers the instruction, so the read has to consume it: a row left collectable would be
/// served again on the next tick sixty seconds later, and every serving starts a five-minute
/// patching warning on the host. Naming it a command is the honest description of what it does; the
/// route stays a GET because it is polled once a minute per logged-in host and a GET is what each
/// agent's short-budget <c>get_with_retry</c> is for — a POST there would take the retry budget
/// sized for reporting something that already happened.
/// </para>
/// <para>
/// <strong><see cref="IAgentScopedRequest"/> is what makes the route safe.</strong> The serial
/// number arrives in the query string and <c>RequireAgentIdentityAttribute</c> compares it against
/// the certificate CN nginx verified, so one enrolled agent cannot collect — and thereby cancel —
/// another host's instructions.
/// </para>
/// </remarks>
public record ClaimForcedPatchRunsCommand(string SerialNumber)
    : IRequest<IReadOnlyList<ForcedPatchRunDto>>, IAgentScopedRequest;
