using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class AgentPackageRepository : IAgentPackageRepository
{
    private readonly ApplicationDbContext _context;

    public AgentPackageRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<AgentPackage?> GetLatestByPlatformAsync(string platform, CancellationToken cancellationToken) =>
        _context.AgentPackages
            .Where(p => p.Platform == platform)
            .OrderByDescending(p => p.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<AgentPackage?> GetByPlatformAndVersionAsync(string platform, string version, CancellationToken cancellationToken) =>
        _context.AgentPackages.FirstOrDefaultAsync(p => p.Platform == platform && p.Version == version, cancellationToken);

    // Small catalog by nature (a handful of platforms, each with a handful of published
    // versions) — grouping in memory after one unfiltered fetch is simpler than a per-platform
    // "top 1" SQL query and won't meaningfully cost more.
    public async Task<IReadOnlyList<AgentPackage>> GetLatestPerPlatformAsync(CancellationToken cancellationToken)
    {
        var all = await _context.AgentPackages.AsNoTracking().ToListAsync(cancellationToken);

        return all
            .GroupBy(p => p.Platform, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.CreatedAtUtc).First())
            .OrderBy(p => p.Platform, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task AddAsync(AgentPackage package, CancellationToken cancellationToken) =>
        await _context.AgentPackages.AddAsync(package, cancellationToken);
}
