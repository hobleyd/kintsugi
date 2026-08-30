using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Applications;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class InstalledApplicationRepository : IInstalledApplicationRepository
{
    private readonly ApplicationDbContext _context;

    public InstalledApplicationRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<InstalledApplication>> GetByHostIdAsync(Guid hostId, CancellationToken cancellationToken) =>
        await _context.InstalledApplications.Where(a => a.HostId == hostId).ToListAsync(cancellationToken);

    public async Task<InstalledApplication?> GetByHostIdAndNameAsync(Guid hostId, string name, CancellationToken cancellationToken) =>
        await _context.InstalledApplications.FirstOrDefaultAsync(a => a.HostId == hostId && a.Name == name, cancellationToken);

    public async Task AddRangeAsync(IEnumerable<InstalledApplication> applications, CancellationToken cancellationToken) =>
        await _context.InstalledApplications.AddRangeAsync(applications, cancellationToken);

    public void RemoveRange(IEnumerable<InstalledApplication> applications) =>
        _context.InstalledApplications.RemoveRange(applications);

    public async Task<IReadOnlyList<ApplicationSummaryDto>> GetSummariesAsync(CancellationToken cancellationToken)
    {
        // Grouped at the database level by (name, parent) — bounded by how many distinct
        // applications and package managers exist, not by how many hosts report them (a host
        // contributes at most one row per distinct name, so summing each group's host count back
        // up per name is exact, not a double-count). Keeps this cheap across a fleet of any size.
        var rows = await _context.InstalledApplications
            .GroupBy(a => new { a.Name, a.ParentApplicationId })
            .Select(g => new { g.Key.Name, g.Key.ParentApplicationId, HostCount = g.Select(a => a.HostId).Distinct().Count() })
            .ToListAsync(cancellationToken);

        var parentIds = rows.Where(r => r.ParentApplicationId.HasValue).Select(r => r.ParentApplicationId!.Value).Distinct().ToList();
        var parentNames = await _context.InstalledApplications
            .Where(a => parentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name })
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

        // Names of every host reporting each application, for filtering by host in the UI — a
        // separate query grouped at the database level, same reasoning as HostCount above.
        var hostNamesByAppName = (await _context.InstalledApplications
            .Join(_context.Hosts, a => a.HostId, h => h.Id, (a, h) => new { a.Name, h.Hostname })
            .Distinct()
            .ToListAsync(cancellationToken))
            .GroupBy(x => x.Name)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Hostname).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList());

        var byName = rows
            .GroupBy(r => r.Name)
            .Select(g => new
            {
                Name = g.Key,
                HostCount = g.Sum(r => r.HostCount),
                // A name is treated as a child if any row reporting it named a
                // parent that resolves to another app in this same dataset.
                ParentName = g
                    .Select(r => r.ParentApplicationId.HasValue && parentNames.TryGetValue(r.ParentApplicationId.Value, out var parentName)
                        ? parentName
                        : null)
                    .FirstOrDefault(name => name is not null)
            })
            .ToList();

        IReadOnlyList<string> HostNamesFor(string name) =>
            hostNamesByAppName.TryGetValue(name, out var names) ? names : Array.Empty<string>();

        var childrenByParentName = byName
            .Where(x => x.ParentName is not null)
            .ToLookup(
                x => x.ParentName!,
                x => new ApplicationSummaryDto(x.Name, x.HostCount, HostNamesFor(x.Name), Array.Empty<ApplicationSummaryDto>()));

        return byName
            .Where(x => x.ParentName is null)
            .OrderBy(x => x.Name)
            .Select(x => new ApplicationSummaryDto(
                x.Name,
                x.HostCount,
                HostNamesFor(x.Name),
                childrenByParentName[x.Name].OrderBy(c => c.Name).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<ApplicationVersionVariantDto>> GetApplicationVersionVariantsAsync(CancellationToken cancellationToken)
    {
        // Grouped at the database level by (name, version, OS, parent) — a large fleet can have a
        // huge number of (host, application) rows but a small, bounded number of distinct
        // version/OS/parent combinations, so this is what actually gets pulled into memory.
        var rows = await _context.InstalledApplications
            .Join(_context.Hosts, a => a.HostId, h => h.Id, (a, h) => new { a.Name, a.Version, a.ParentApplicationId, a.ApplicationIdentifier, h.OperatingSystem })
            .GroupBy(x => new { x.Name, x.Version, x.ParentApplicationId, x.ApplicationIdentifier, x.OperatingSystem })
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

        var parentIds = rows.Where(r => r.ParentApplicationId.HasValue).Select(r => r.ParentApplicationId!.Value).Distinct().ToList();
        var parentNames = await _context.InstalledApplications
            .Where(a => parentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name })
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

        return rows
            .Select(r => new ApplicationVersionVariantDto(
                r.Name,
                r.ParentApplicationId.HasValue && parentNames.TryGetValue(r.ParentApplicationId.Value, out var name) ? name : null,
                r.OperatingSystem,
                r.Version,
                r.ApplicationIdentifier))
            .ToList();
    }
}
