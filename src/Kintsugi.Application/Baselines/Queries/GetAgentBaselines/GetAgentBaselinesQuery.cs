using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Queries.GetAgentBaselines;

/// <summary>
/// What an agent fetches at each hourly check-in (<c>GET /api/baseline</c>): the latest signed version of
/// every baseline assigned to it, with what the server already holds for this host so the agent can send
/// only what changed.
/// </summary>
/// <remarks>
/// <see cref="IAgentScopedRequest"/> is what makes the route safe: the serial number arrives in the query
/// string and <c>RequireAgentIdentityAttribute</c> compares it with the certificate CN nginx verified, so
/// a stolen certificate fetches only its own device's baselines.
/// </remarks>
public record GetAgentBaselinesQuery(string SerialNumber) : IRequest<AgentBaselinesDto>, IAgentScopedRequest;
