using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class UpgradePathRepository : IUpgradePathRepository
{
    private readonly ApplicationDbContext _context;

    public UpgradePathRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    // Case-insensitive on ApplicationName: the scan planner groups installed-application variants
    // case-insensitively (PrepareUpgradePathScanQueryHandler), so the casing a given scan settles on
    // for e.g. a Homebrew cask can differ run to run depending on which host's data it happens to
    // see first. An exact, case-sensitive match here would miss an existing row stored under a
    // different casing and insert a brand new one instead of updating it — which, since the DB's
    // uniqueness constraint is itself case-sensitive, silently accumulates duplicate rows for the
    // same application until GetSummariesAsync's dictionary keying (also case-insensitive) collides
    // on them and throws. See BuildByNameAndPlatformLookup for the read-side half of this.
    public Task<UpgradePath?> GetAsync(string applicationName, string platform, CancellationToken cancellationToken) =>
        _context.UpgradePaths.FirstOrDefaultAsync(
            p => p.ApplicationName.ToLower() == applicationName.ToLower() && p.Platform == platform,
            cancellationToken);

    public Task<UpgradePath?> GetByApplicationIdentifierAsync(string applicationIdentifier, CancellationToken cancellationToken) =>
        _context.UpgradePaths.FirstOrDefaultAsync(p => p.ApplicationIdentifier == applicationIdentifier, cancellationToken);

    public async Task AddAsync(UpgradePath upgradePath, CancellationToken cancellationToken) =>
        await _context.UpgradePaths.AddAsync(upgradePath, cancellationToken);

    public async Task<IReadOnlyList<UpgradePath>> GetAllForApplicationAsync(string applicationName, CancellationToken cancellationToken) =>
        await _context.UpgradePaths
            .Where(p => p.ApplicationName.ToLower() == applicationName.ToLower())
            .ToListAsync(cancellationToken);

    public void Remove(UpgradePath upgradePath) => _context.UpgradePaths.Remove(upgradePath);

    public Task<string?> FindExistingSignatureForScriptAsync(string script, CancellationToken cancellationToken) =>
        _context.UpgradePaths
            .Where(p => p.Script == script && p.ScriptSignature != null)
            .Select(p => p.ScriptSignature)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<UpgradePath>> GetUnsignedRowsWithScriptAsync(string script, CancellationToken cancellationToken) =>
        await _context.UpgradePaths
            .Where(p => p.Script == script && p.ScriptSignature == null)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<UpgradeStatusDto>> GetStatusesAsync(string serialNumber, CancellationToken cancellationToken)
    {
        // Scoped to one host, so this is bounded by how many applications one machine has — small
        // regardless of fleet size.
        var installed = await _context.InstalledApplications
            .Join(_context.Hosts, a => a.HostId, h => h.Id, (a, h) => new
            {
                a.Name,
                a.Version,
                a.ApplicationIdentifier,
                h.Hostname,
                h.SerialNumber,
                h.OperatingSystem
            })
            .Where(x => x.SerialNumber == serialNumber)
            .ToListAsync(cancellationToken);

        var upgradePaths = await _context.UpgradePaths.ToListAsync(cancellationToken);
        var byNameAndPlatform = BuildByNameAndPlatformLookup(upgradePaths);

        var results = new List<UpgradeStatusDto>();

        foreach (var app in installed)
        {
            var platform = PlatformBucket.From(app.OperatingSystem);
            var key = (app.Name.ToLowerInvariant(), platform);

            if (!byNameAndPlatform.TryGetValue(key, out var path)
                && !byNameAndPlatform.TryGetValue((app.Name.ToLowerInvariant(), PlatformBucket.Generic), out path))
            {
                continue;
            }

            var updateAvailable = ComputeUpdateAvailable(path, app.Version);

            results.Add(new UpgradeStatusDto(
                app.Name,
                app.Hostname,
                app.SerialNumber,
                app.Version,
                path.LatestVersion,
                updateAvailable,
                path.Status,
                path.Method,
                path.DownloadUrl,
                path.Command,
                path.Instructions,
                path.SourceUrl,
                path.Notes,
                path.CheckedUtc,
                path.Script,
                app.ApplicationIdentifier,
                path.ScriptSignature,
                path.CommandSignature));
        }

        return results
            .OrderByDescending(r => r.UpdateAvailable)
            .ThenBy(r => r.ApplicationName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<UpgradePathSummaryDto>> GetSummariesAsync(CancellationToken cancellationToken)
    {
        // Host identity (not just an aggregate count) has to survive into this method now, so the
        // Applications page can flag exactly which hosts are behind on a given application — same
        // full materialization GetAppUpdateCountsByHostAsync already does below, and bounded the
        // same way (by total installed-application rows, not by anything larger).
        var installed = await _context.InstalledApplications
            .Join(_context.Hosts, a => a.HostId, h => h.Id, (a, h) => new { a.Name, a.Version, a.HostId, h.Hostname, h.OperatingSystem })
            .ToListAsync(cancellationToken);

        var upgradePaths = await _context.UpgradePaths.ToListAsync(cancellationToken);
        var byNameAndPlatform = BuildByNameAndPlatformLookup(upgradePaths);

        var results = new List<UpgradePathSummaryDto>();

        foreach (var appGroup in installed.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var platformGroup in appGroup.GroupBy(x => PlatformBucket.From(x.OperatingSystem)))
            {
                var platform = platformGroup.Key;
                var key = (appGroup.Key.ToLowerInvariant(), platform);

                if (!byNameAndPlatform.TryGetValue(key, out var path)
                    && !byNameAndPlatform.TryGetValue((appGroup.Key.ToLowerInvariant(), PlatformBucket.Generic), out path))
                {
                    continue;
                }

                var hostCount = platformGroup.Select(x => x.HostId).Distinct().Count();
                // No LatestVersion means nothing concrete is known yet (an unresearched app, a
                // self-update command, or an unrecognized package manager) — report 0/0 ("unknown")
                // rather than guessing, instead of counting those hosts as up to date.
                var upToDateCount = path.LatestVersion is null
                    ? 0
                    : platformGroup.Where(x => !VersionComparer.IsNewer(path.LatestVersion, x.Version)).Select(x => x.HostId).Distinct().Count();
                var hostNamesNeedingUpdate = path.LatestVersion is null
                    ? Array.Empty<string>()
                    : platformGroup
                        .Where(x => VersionComparer.IsNewer(path.LatestVersion, x.Version))
                        .Select(x => x.Hostname)
                        .Distinct()
                        .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                var updateAvailableCount = path.LatestVersion is null ? 0 : hostCount - upToDateCount;

                results.Add(new UpgradePathSummaryDto(
                    appGroup.Key,
                    // path.Platform, not the loop's platform bucket: for a package-manager-managed
                    // row (always stored under PlatformBucket.Generic) those two differ — reporting
                    // the OS bucket here instead of the row's real key would round-trip back to the
                    // API (e.g. the Applications page's per-row instructions panel) as a platform
                    // that can never match the item PrepareUpgradePathScanQueryHandler builds for it.
                    path.Platform,
                    path.Status,
                    path.LatestVersion,
                    path.Method,
                    path.DownloadUrl,
                    path.Command,
                    path.Instructions,
                    path.SourceUrl,
                    path.Notes,
                    path.CheckedUtc,
                    hostCount,
                    upToDateCount,
                    updateAvailableCount,
                    hostNamesNeedingUpdate,
                    path.Script,
                    path.ScriptSignature));
            }
        }

        return results
            .OrderByDescending(r => r.UpdateAvailableHostCount)
            .ThenBy(r => r.ApplicationName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<UpgradePath>> GetScriptUpgradePathsAsync(CancellationToken cancellationToken) =>
        await _context.UpgradePaths
            .Where(p => p.Method == UpgradeMethod.Script && p.Script != null)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> GetAppUpdateCountsByHostAsync(CancellationToken cancellationToken)
    {
        // Fleet-wide, but bounded by total installed-application rows (like GetSummariesAsync's
        // join below), not by anything larger — safe to pull into memory for the per-host matching.
        var installed = await _context.InstalledApplications
            .Join(_context.Hosts, a => a.HostId, h => h.Id, (a, h) => new { a.HostId, a.Name, a.Version, h.OperatingSystem })
            .ToListAsync(cancellationToken);

        var upgradePaths = await _context.UpgradePaths.ToListAsync(cancellationToken);
        var byNameAndPlatform = BuildByNameAndPlatformLookup(upgradePaths);

        var counts = new Dictionary<Guid, int>();

        foreach (var app in installed)
        {
            var platform = PlatformBucket.From(app.OperatingSystem);
            var key = (app.Name.ToLowerInvariant(), platform);

            if (!byNameAndPlatform.TryGetValue(key, out var path)
                && !byNameAndPlatform.TryGetValue((app.Name.ToLowerInvariant(), PlatformBucket.Generic), out path))
            {
                continue;
            }

            if (!ComputeUpdateAvailable(path, app.Version))
            {
                continue;
            }

            counts[app.HostId] = counts.GetValueOrDefault(app.HostId) + 1;
        }

        return counts;
    }

    // ApplicationName is matched case-insensitively (see GetAsync's own comment for why), but the
    // DB's uniqueness constraint on (ApplicationName, Platform) is case-sensitive, so two rows for
    // what's really the same application can exist at once, differing only by casing — a plain
    // ToDictionary over this key would throw on that collision. Picking the most recently checked
    // row per key means a live duplicate (however it got there) degrades to "the stale one is
    // ignored" instead of a 500 on every page load; it doesn't delete anything.
    private static Dictionary<(string Name, string Platform), UpgradePath> BuildByNameAndPlatformLookup(IEnumerable<UpgradePath> upgradePaths) =>
        upgradePaths
            .GroupBy(p => (p.ApplicationName.ToLowerInvariant(), p.Platform))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.CheckedUtc).First());

    private static bool ComputeUpdateAvailable(UpgradePath path, string installedVersion) =>
        // LatestVersion is null whenever nothing concrete is known yet (a self-update command, an
        // unrecognized package manager, or an app not yet researched) — VersionComparer.IsNewer
        // already returns false for a null latest, so those correctly never read as "available".
        // A package-manager-managed app (e.g. Homebrew) that reports its own catalog version does
        // carry a real LatestVersion and is compared normally, same as any AI-researched one.
        VersionComparer.IsNewer(path.LatestVersion, installedVersion);
}
