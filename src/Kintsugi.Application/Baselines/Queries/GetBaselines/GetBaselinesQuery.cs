using MediatR;

namespace Kintsugi.Application.Baselines.Queries.GetBaselines;

/// <summary>Every baseline with its compliance totals — the Baselines screen's list.</summary>
public record GetBaselinesQuery : IRequest<IReadOnlyList<BaselineSummaryDto>>;
