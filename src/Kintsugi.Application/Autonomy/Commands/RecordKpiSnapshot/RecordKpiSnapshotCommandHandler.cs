using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Autonomy.Commands.RecordKpiSnapshot;

public class RecordKpiSnapshotCommandHandler : IRequestHandler<RecordKpiSnapshotCommand, int>
{
    private readonly IHostRepository _hosts;
    private readonly IPatchFailureRepository _failures;
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly IHostEventRepository _events;
    private readonly IKpiSnapshotRepository _snapshots;
    private readonly IUnitOfWork _unitOfWork;

    public RecordKpiSnapshotCommandHandler(
        IHostRepository hosts,
        IPatchFailureRepository failures,
        IUpgradePathRepository upgradePaths,
        IHostEventRepository events,
        IKpiSnapshotRepository snapshots,
        IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _failures = failures;
        _upgradePaths = upgradePaths;
        _events = events;
        _snapshots = snapshots;
        _unitOfWork = unitOfWork;
    }

    /// <returns>How many snapshots were written: zero when today already has them or there is no fleet.</returns>
    public async Task<int> Handle(RecordKpiSnapshotCommand request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (await _snapshots.ExistsForDateAsync(now, cancellationToken))
        {
            return 0;
        }

        var inputs = await AutonomyInputs.LoadAsync(_hosts, _failures, _upgradePaths, cancellationToken, _events);
        if (inputs.Hosts.Count == 0)
        {
            return 0;
        }

        var values = AutonomyKpis.Compute(inputs.Hosts, inputs.OpenFailures, inputs.AppUpdates, inputs.Causes(now), inputs.Health, now);
        var rows = values.Select(v => KpiSnapshot.Record(now, v.Definition.Id, v.Value, v.Numerator, v.Denominator)).ToList();

        await _snapshots.AddRangeAsync(rows, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }
}
