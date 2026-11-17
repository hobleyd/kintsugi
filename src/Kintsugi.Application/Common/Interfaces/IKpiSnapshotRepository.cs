using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IKpiSnapshotRepository
{
    Task<bool> ExistsForDateAsync(DateTimeOffset dateUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<KpiSnapshot>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    Task AddRangeAsync(IEnumerable<KpiSnapshot> snapshots, CancellationToken cancellationToken);
}
