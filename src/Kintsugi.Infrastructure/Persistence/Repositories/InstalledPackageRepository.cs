using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="IInstalledPackageRepository" />
/// <remarks>
/// The ecosystem is resolved in memory rather than in SQL, because <c>OsvEcosystem.For</c> is
/// where that vocabulary lives and a LINQ translation of it would be a second copy to keep in
/// step. The set it runs over is one row per (host, package), which is large but is read once per
/// query and grouped immediately.
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

    public async Task<IReadOnlyList<PackageTriple>> GetDistinctPackageTriplesAsync(CancellationToken cancellationToken)
    {
        var rows = await LiveRowsAsync(cancellationToken);

        return rows
            .Select(r => new { Ecosystem = OsvEcosystem.For(r.OsId, r.OsVersionId), r.Name, r.Version })
            .Where(r => r.Ecosystem is not null)
            .Select(r => new PackageTriple(r.Ecosystem!, r.Name, r.Version))
            .Distinct()
            .ToList();
    }

    public async Task<int> GetUnsupportedPackageHostCountAsync(CancellationToken cancellationToken)
    {
        var hosts = await (
            from package in _context.InstalledPackages.AsNoTracking()
            join host in _context.Hosts.AsNoTracking() on package.HostId equals host.Id
            where host.DeletedAtUtc == null
            select new { host.Id, host.OperatingSystemId, host.OperatingSystemVersionId })
            .Distinct()
            .ToListAsync(cancellationToken);

        return hosts.Count(h => OsvEcosystem.For(h.OperatingSystemId, h.OperatingSystemVersionId) is null);
    }

    public async Task<IReadOnlyDictionary<PackageTriple, int>> GetHostCountsByTripleAsync(CancellationToken cancellationToken)
    {
        var rows = await LiveRowsAsync(cancellationToken);

        return rows
            .Select(r => new { Ecosystem = OsvEcosystem.For(r.OsId, r.OsVersionId), r.Name, r.Version, r.HostId })
            .Where(r => r.Ecosystem is not null)
            .GroupBy(r => new PackageTriple(r.Ecosystem!, r.Name, r.Version))
            .ToDictionary(g => g.Key, g => g.Select(r => r.HostId).Distinct().Count());
    }

    public async Task<IReadOnlyList<Guid>> GetHostIdsForTriplesAsync(
        IReadOnlyCollection<PackageTriple> triples, CancellationToken cancellationToken)
    {
        if (triples.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var wanted = triples.ToHashSet();
        var rows = await LiveRowsAsync(cancellationToken);

        return rows
            .Select(r => new { Ecosystem = OsvEcosystem.For(r.OsId, r.OsVersionId), r.Name, r.Version, r.HostId })
            .Where(r => r.Ecosystem is not null && wanted.Contains(new PackageTriple(r.Ecosystem!, r.Name, r.Version)))
            .Select(r => r.HostId)
            .Distinct()
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetHostnamesForTripleAsync(PackageTriple triple, CancellationToken cancellationToken)
    {
        var rows = await (
            from package in _context.InstalledPackages.AsNoTracking()
            join host in _context.Hosts.AsNoTracking() on package.HostId equals host.Id
            where host.DeletedAtUtc == null && package.Name == triple.Name && package.Version == triple.Version
            select new { host.Hostname, host.OperatingSystemId, host.OperatingSystemVersionId })
            .Distinct()
            .ToListAsync(cancellationToken);

        return rows
            .Where(r => OsvEcosystem.For(r.OperatingSystemId, r.OperatingSystemVersionId) == triple.Ecosystem)
            .Select(r => r.Hostname)
            .Distinct()
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<PackageRow>> LiveRowsAsync(CancellationToken cancellationToken) =>
        await (
            from package in _context.InstalledPackages.AsNoTracking()
            join host in _context.Hosts.AsNoTracking() on package.HostId equals host.Id
            where host.DeletedAtUtc == null
            select new PackageRow(
                package.HostId, package.Name, package.Version, host.OperatingSystemId, host.OperatingSystemVersionId))
            .ToListAsync(cancellationToken);

    private record PackageRow(Guid HostId, string Name, string Version, string? OsId, string? OsVersionId);
}
