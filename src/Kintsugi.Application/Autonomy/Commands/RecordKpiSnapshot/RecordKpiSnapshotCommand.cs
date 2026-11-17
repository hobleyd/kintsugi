using MediatR;

namespace Kintsugi.Application.Autonomy.Commands.RecordKpiSnapshot;

/// <summary>Records today's value of every KPI, once a day. Does nothing if today already has them.</summary>
public record RecordKpiSnapshotCommand : IRequest<int>;
