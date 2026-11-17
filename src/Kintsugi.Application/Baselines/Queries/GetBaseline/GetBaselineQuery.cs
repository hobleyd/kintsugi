using MediatR;

namespace Kintsugi.Application.Baselines.Queries.GetBaseline;

/// <summary>One baseline: its versions (for review and signing), the signed version's items with
/// per-item compliance, and per-host compliance for the drill-down.</summary>
public record GetBaselineQuery(Guid BaselineId) : IRequest<BaselineDetailDto>;
