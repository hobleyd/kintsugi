using MediatR;

namespace Kintsugi.Application.Autonomy.Queries.GetAutonomy;

/// <param name="Period">"7d", "30d" or "month".</param>
/// <param name="Month">"yyyy-MM", for the monthly period; the current month when missing or wrong.</param>
public record GetAutonomyQuery(string Period, string? Month = null) : IRequest<AutonomyDto>;
