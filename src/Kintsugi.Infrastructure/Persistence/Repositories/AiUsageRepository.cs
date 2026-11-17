using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class AiUsageRepository : IAiUsageRepository
{
    private readonly ApplicationDbContext _context;

    public AiUsageRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<AiBudgetSettings?> GetBudgetAsync(CancellationToken cancellationToken) =>
        _context.AiBudgetSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddBudgetAsync(AiBudgetSettings settings, CancellationToken cancellationToken) =>
        await _context.AiBudgetSettings.AddAsync(settings, cancellationToken);

    public async Task AddUsageAsync(AiUsageRecord record, CancellationToken cancellationToken) =>
        await _context.AiUsage.AddAsync(record, cancellationToken);

    public async Task<IReadOnlyList<AiUsageRecord>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        await _context.AiUsage.AsNoTracking()
            .Where(r => r.OccurredUtc >= sinceUtc)
            .OrderBy(r => r.OccurredUtc)
            .ToListAsync(cancellationToken);

    public async Task<AiSpendTotals> GetSpendSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var month = _context.AiUsage.AsNoTracking().Where(r => r.OccurredUtc >= sinceUtc);

        var priced = await month.Where(r => r.EstimatedCostUsd != null).SumAsync(r => r.EstimatedCostUsd!.Value, cancellationToken);

        // Grouped on a constant so the three unpriced aggregates are one round trip; an empty month
        // yields no group, hence the default.
        var unpriced = await month.Where(r => r.EstimatedCostUsd == null)
            .GroupBy(_ => 1)
            .Select(g => new { Calls = g.Count(), Input = g.Sum(r => r.InputTokens), Output = g.Sum(r => r.OutputTokens) })
            .FirstOrDefaultAsync(cancellationToken);

        return new AiSpendTotals(priced, unpriced?.Calls ?? 0, unpriced?.Input ?? 0, unpriced?.Output ?? 0);
    }
}
