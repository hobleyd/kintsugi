using MediatR;

namespace Kintsugi.Application.Baselines.Queries.GetBaselineHost;

/// <summary>The per-device drill-down: every item of a baseline's signed version as one host last
/// reported it, and the facts that host reported.</summary>
public record GetBaselineHostQuery(Guid BaselineId, Guid HostId) : IRequest<BaselineHostDetailDto>;
