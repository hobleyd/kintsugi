using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="IInstalledPackageRepository" />
/// <remarks>
/// <para>
/// <b>Every query groups in the database first, and that is not an optimization.</b> The raw
/// table is one row per (host, package) — a hundred Ubuntu hosts at ~1500 source packages each is
/// 150,000 rows — and the Vulnerabilities screen touches two of these methods on every page view.
/// Loading that cross product into memory to group it would make the screen slow in a way that
/// reads as a server problem.
/// </para>
/// <para>
/// The OSV ecosystem is still resolved in memory, because <c>OsvEcosystem.For</c> is where that
/// vocabulary lives and a LINQ translation of it would be a second copy to keep in step. That
/// costs nothing now: the grouping key includes the host's two os-release columns, so what comes
/// back is one row per distinct (distribution, package, version) — thousands at most — and the
/// mapping runs over that rather than over the cross product.
/// </para>
/// </remarks>
public class InstalledPackageRepository : IInstalledPackageRepository
{
    private readonly ApplicationDbContext _context;

    public InstalledPackageRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task ReplaceForHostAsync(Guid hostId, IEnumerable<InstalledPackage> packages, CancellationToken cancellationToken)
    {
        // ExecuteDelete rather than loading a thousand rows to mark them deleted: this runs on
        // every hourly inventory report from every Linux host.
        await _context.InstalledPackages
            .Where(p => p.HostId == hostId)
            .ExecuteDeleteAsync(cancellationToken);

        await _context.InstalledPackages.AddRangeAsync(packages, cancellationToken);
    }

    public async Task<IReadOnlyList<PackageTriple>> GetDistinctPackageTriplesAsync(CancellationToken cancellationToken) =>
        (await GroupedAsync(cancellationToken))
            .Select(g => Triple(g))
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct()
            .ToList();

    public async Task<int> GetUnsupportedPackageHostCountAsync(CancellationToken cancellationToken)
    {
        // Distinct distributions rather than distinct hosts in memory: the number of os-release
        // pairs across a fleet is tiny, and each carries its own host count.
        var byDistribution = await (
            from package in _context.InstalledPackages.AsNoTracking()
            join host in _context.Hosts.AsNoTracking() on package.HostId equals host.Id
            where host.DeletedAtUtc == null
            group host.Id by new { host.OperatingSystemId, host.OperatingSystemVersionId } into grouped
            select new
            {
                grouped.Key.OperatingSystemId,
                grouped.Key.OperatingSystemVersionId,
                HostCount = grouped.Distinct().Count()
            }).ToListAsync(cancellationToken);

        return byDistribution
            .Where(d => OsvEcosystem.For(d.OperatingSystemId, d.OperatingSystemVersionId) is null)
            .Sum(d => d.HostCount);
    }

    public async Task<IReadOnlyDictionary<PackageTriple, int>> GetHostCountsByTripleAsync(CancellationToken cancellationToken)
    {
        var counts = new Dictionary<PackageTriple, int>();

        foreach (var group in await GroupedAsync(cancellationToken))
        {
            var triple = Triple(group);
            if (triple is null)
            {
                continue;
            }

            // Summed rather than assigned: two distributions can map to one ecosystem (a point
            // release and its major, say), so the same triple can arrive from two groups.
            counts[triple] = counts.GetValueOrDefault(triple) + group.HostCount;
        }

        return counts;
    }

    public async Task<IReadOnlyList<Guid>> GetHostIdsForTriplesAsync(
        IReadOnlyCollection<PackageTriple> triples, CancellationToken cancellationToken)
    {
        if (triples.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        // Filtered in the database on the package half of the triple first, so only rows that
        // could possibly match are read; the ecosystem half is checked in memory afterwards,
        // since it is the part OsvEcosystem has to resolve.
        var names = triples.Select(t => t.Name).Distinct().ToList();
        var versions = triples.Select(t => t.Version).Distinct().ToList();
        var wanted = triples.ToHashSet();

        var rows = await (
            from package in _context.InstalledPackages.AsNoTracking()
            join host in _context.Hosts.AsNoTracking() on package.HostId equals host.Id
            where host.DeletedAtUtc == null
                  && names.Contains(package.Name)
                  && versions.Contains(package.Version)
            select new
            {
                package.HostId,
                package.Name,
                package.Version,
                host.OperatingSystemId,
                host.OperatingSystemVersionId
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        return rows
            .Where(r =>
            {
                var ecosystem = OsvEcosystem.For(r.OperatingSystemId, r.OperatingSystemVersionId);
                return ecosystem is not null && wanted.Contains(new PackageTriple(ecosystem, r.Name, r.Version));
            })
            .Select(r => r.HostId)
            .Distinct()
            .ToList();
    }

    public async Task<IReadOnlyList<HostPackageTriple>> GetHostPackageTriplesAsync(
        IReadOnlyCollection<string> names, IReadOnlyCollection<string> versions, CancellationToken cancellationToken)
    {
        if (names.Count == 0 || versions.Count == 0)
        {
            return Array.Empty<HostPackageTriple>();
        }

        // Filtered on the package half in the database first, the same way GetHostIdsForTriplesAsync
        // is — only rows a caller already knows have a CVE match are worth resolving an ecosystem
        // for, so this never touches the full host × package cross product.
        var rows = await (
            from package in _context.InstalledPackages.AsNoTracking()
            join host in _context.Hosts.AsNoTracking() on package.HostId equals host.Id
            where host.DeletedAtUtc == null
                  && names.Contains(package.Name)
                  && versions.Contains(package.Version)
            select new
            {
                package.HostId,
                package.Name,
                package.Version,
                host.OperatingSystemId,
                host.OperatingSystemVersionId
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new { r.HostId, r.Name, r.Version, Ecosystem = OsvEcosystem.For(r.OperatingSystemId, r.OperatingSystemVersionId) })
            .Where(r => r.Ecosystem is not null)
            .Select(r => new HostPackageTriple(r.HostId, new PackageTriple(r.Ecosystem!, r.Name, r.Version)))
            .ToList();
    }

    /// <summary>
    /// One row per (distribution, package, version) across live hosts, with how many hosts carry
    /// it — grouped by the database, so the result is thousands of rows rather than the
    /// host × package cross product.
    /// </summary>
    private async Task<List<PackageGroup>> GroupedAsync(CancellationToken cancellationToken) =>
        await (
            from package in _context.InstalledPackages.AsNoTracking()
            join host in _context.Hosts.AsNoTracking() on package.HostId equals host.Id
            where host.DeletedAtUtc == null
            group host.Id by new
            {
                host.OperatingSystemId,
                host.OperatingSystemVersionId,
                package.Name,
                package.Version
            } into grouped
            select new PackageGroup(
                grouped.Key.OperatingSystemId,
                grouped.Key.OperatingSystemVersionId,
                grouped.Key.Name,
                grouped.Key.Version,
                grouped.Distinct().Count()))
            .ToListAsync(cancellationToken);

    private static PackageTriple? Triple(PackageGroup group)
    {
        var ecosystem = OsvEcosystem.For(group.OsId, group.OsVersionId);
        return ecosystem is null ? null : new PackageTriple(ecosystem, group.Name, group.Version);
    }

    private record PackageGroup(string? OsId, string? OsVersionId, string Name, string Version, int HostCount);
}
