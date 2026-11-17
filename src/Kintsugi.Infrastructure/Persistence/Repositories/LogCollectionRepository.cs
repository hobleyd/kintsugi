using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.LogCollections;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class LogCollectionRepository : ILogCollectionRepository
{
    private readonly ApplicationDbContext _context;

    public LogCollectionRepository(ApplicationDbContext context) => _context = context;

    public async Task AddAsync(LogCollection collection, CancellationToken cancellationToken) =>
        await _context.LogCollections.AddAsync(collection, cancellationToken);

    public Task<LogCollection?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.LogCollections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<LogCollection?> GetOpenForHostAsync(Guid hostId, CancellationToken cancellationToken) =>
        _context.LogCollections
            .Where(c => c.HostId == hostId && (c.Status == LogCollectionStatus.Requested || c.Status == LogCollectionStatus.Collecting))
            .OrderByDescending(c => c.RequestedUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<LogCollection?> GetCollectableForHostAsync(Guid hostId, DateTimeOffset now, CancellationToken cancellationToken) =>
        _context.LogCollections
            .Where(c => c.HostId == hostId && c.Status == LogCollectionStatus.Requested && c.ExpiresUtc > now)
            .OrderBy(c => c.RequestedUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<LogCollectionSummary?> GetLatestSummaryForHostAsync(Guid hostId, CancellationToken cancellationToken) =>
        _context.LogCollections
            .AsNoTracking()
            .Where(c => c.HostId == hostId)
            .OrderByDescending(c => c.RequestedUtc)
            .Select(c => new LogCollectionSummary(
                c.Id, c.HostId, c.Status, c.RequestedUtc, c.RequestedBy, c.PeriodDays, c.Detail, c.ExpiresUtc, c.ClaimedUtc, c.CompletedUtc,
                c.FailureReason, c.EntryCount, c.SizeBytes, c.TruncatedBeforeUtc))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<LogCollection?> GetLatestCompletedForHostAsync(Guid hostId, CancellationToken cancellationToken) =>
        _context.LogCollections
            .AsNoTracking()
            .Where(c => c.HostId == hostId && c.Status == LogCollectionStatus.Completed)
            .OrderByDescending(c => c.RequestedUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task DeleteAsync(LogCollection collection, CancellationToken cancellationToken)
    {
        _context.LogCollections.Remove(collection);
        return Task.CompletedTask;
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) =>
        _context.LogCollections.Where(c => (c.CompletedUtc ?? c.RequestedUtc) < cutoffUtc).ExecuteDeleteAsync(cancellationToken);
}
