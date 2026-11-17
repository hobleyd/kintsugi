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
        //
        // **Names that differ only in case are one application.** An agent reports a name the way
        // its platform spells it — "Slack" from a Mac, "slack" from a Linux package — and every other
        // name comparison here and in the upgrade-path code already ignores case, so this list was the
        // odd one out, showing a product twice with each row holding part of its hosts. The merge keys
        // on the database's own `lower(name)`, used throughout this method, so what is grouped, counted
        // and looked up always agrees with itself.
        var rows = await _context.InstalledApplications
            .GroupBy(a => new { a.Name, a.ParentApplicationId })
            .Select(g => new
            {
                g.Key.Name,
                Lower = g.Key.Name.ToLower(),
                g.Key.ParentApplicationId,
                HostCount = g.Select(a => a.HostId).Distinct().Count(),
            })
            .ToListAsync(cancellationToken);

        // The same count, over every spelling at once. Not the sum of the rows above: a host that has
        // reported two spellings (a stale row from an older agent) is one host, and summing would
        // count it twice.
        var mergedHostCounts = (await _context.InstalledApplications
            .GroupBy(a => new { Lower = a.Name.ToLower(), a.ParentApplicationId })
            .Select(g => new { g.Key.Lower, g.Key.ParentApplicationId, HostCount = g.Select(a => a.HostId).Distinct().Count() })
            .ToListAsync(cancellationToken))
            .ToDictionary(x => (x.Lower, x.ParentApplicationId), x => x.HostCount);

        var parentIds = rows.Where(r => r.ParentApplicationId.HasValue).Select(r => r.ParentApplicationId!.Value).Distinct().ToList();
        var parentLowerById = await _context.InstalledApplications
            .Where(a => parentIds.Contains(a.Id))
            .Select(a => new { a.Id, Lower = a.Name.ToLower() })
            .ToDictionaryAsync(a => a.Id, a => a.Lower, cancellationToken);

        // Names of every host reporting each application, for filtering by host in the UI — a
        // separate query grouped at the database level, same reasoning as HostCount above.
        var hostNamesByLower = (await _context.InstalledApplications
            .Join(_context.Hosts, a => a.HostId, h => h.Id, (a, h) => new { Lower = a.Name.ToLower(), h.Hostname })
            .Distinct()
            .ToListAsync(cancellationToken))
            .GroupBy(x => x.Lower)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Hostname).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList());

        var byName = rows
            .GroupBy(r => r.Lower)
            .Select(g => new
            {
                Lower = g.Key,
                // The spelling most hosts report, so the row reads as the fleet writes it. A tie goes
                // to a mixed-case spelling ("Slack" over "SLACK" and "slack", which are how a
                // package id or a shouting installer writes it), then to ordinal order, so the answer is
                // the same every time.
                Name = g.GroupBy(r => r.Name)
                    .OrderByDescending(spelling => spelling.Sum(r => r.HostCount))
                    .ThenByDescending(spelling => IsMixedCase(spelling.Key))
                    .ThenBy(spelling => spelling.Key, StringComparer.Ordinal)
                    .First().Key,
                HostCount = g.Select(r => r.ParentApplicationId).Distinct().Sum(parent => mergedHostCounts[(g.Key, parent)]),
                // A name is treated as a child if any row reporting it named a
                // parent that resolves to another app in this same dataset.
                ParentLower = g
                    .Select(r => r.ParentApplicationId.HasValue && parentLowerById.TryGetValue(r.ParentApplicationId.Value, out var parentLower)
                        ? parentLower
                        : null)
                    .FirstOrDefault(lower => lower is not null)
            })
            .ToList();

        IReadOnlyList<string> HostNamesFor(string lower) =>
            hostNamesByLower.TryGetValue(lower, out var names) ? names : Array.Empty<string>();

        var childrenByParentLower = byName
            .Where(x => x.ParentLower is not null)
            .ToLookup(
                x => x.ParentLower!,
                x => new ApplicationSummaryDto(x.Name, x.HostCount, HostNamesFor(x.Lower), Array.Empty<ApplicationSummaryDto>()));

        return byName
            .Where(x => x.ParentLower is null)
            .OrderBy(x => x.Name)
            .Select(x => new ApplicationSummaryDto(
                x.Name,
                x.HostCount,
                HostNamesFor(x.Lower),
                childrenByParentLower[x.Lower].OrderBy(c => c.Name).ToList()))
            .ToList();
    }

    /// <summary>Whether <paramref name="name"/> has both capitals and lower case in it.</summary>
    private static bool IsMixedCase(string name) => name.Any(char.IsUpper) && name.Any(char.IsLower);

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
