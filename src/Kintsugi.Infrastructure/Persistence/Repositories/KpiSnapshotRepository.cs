using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class KpiSnapshotRepository : IKpiSnapshotRepository
{
    private readonly ApplicationDbContext _context;

    public KpiSnapshotRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<bool> ExistsForDateAsync(DateTimeOffset dateUtc, CancellationToken cancellationToken)
    {
        var day = new DateTimeOffset(dateUtc.UtcDateTime.Date, TimeSpan.Zero);
        return _context.KpiSnapshots.AnyAsync(s => s.SnapshotDateUtc == day, cancellationToken);
    }

    public async Task<IReadOnlyList<KpiSnapshot>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        await _context.KpiSnapshots
            .AsNoTracking()
            .Where(s => s.SnapshotDateUtc >= sinceUtc)
            .OrderBy(s => s.SnapshotDateUtc)
            .ToListAsync(cancellationToken);

    public async Task AddRangeAsync(IEnumerable<KpiSnapshot> snapshots, CancellationToken cancellationToken) =>
        await _context.KpiSnapshots.AddRangeAsync(snapshots, cancellationToken);
}
