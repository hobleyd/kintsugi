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
                a.ParentApplicationId,
                h.Hostname,
                h.SerialNumber,
                h.OperatingSystem
            })
            .Where(x => x.SerialNumber == serialNumber)
            .ToListAsync(cancellationToken);

        var upgradePaths = await _context.UpgradePaths.ToListAsync(cancellationToken);
        var byNameAndPlatform = BuildByNameAndPlatformLookup(upgradePaths);
        var packageManagerNames = await LoadPackageManagerNamesAsync(installed.Select(x => x.ParentApplicationId), cancellationToken);

        var results = new List<UpgradeStatusDto>();

        foreach (var app in installed)
        {
            var path = ResolvePath(byNameAndPlatform, app.Name, app.OperatingSystem, PackageManagerOf(packageManagerNames, app.ParentApplicationId));
            if (path is null)
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
            .Join(_context.Hosts, a => a.HostId, h => h.Id, (a, h) => new { a.Name, a.Version, a.HostId, a.ParentApplicationId, h.Hostname, h.OperatingSystem })
            .ToListAsync(cancellationToken);

        var upgradePaths = await _context.UpgradePaths.ToListAsync(cancellationToken);
        var byNameAndPlatform = BuildByNameAndPlatformLookup(upgradePaths);
        var packageManagerNames = await LoadPackageManagerNamesAsync(installed.Select(x => x.ParentApplicationId), cancellationToken);

        var results = new List<UpgradePathSummaryDto>();

        foreach (var appGroup in installed.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Grouped by the row each installation actually resolves to, not by the host's OS
            // bucket: a package-manager-managed installation resolves to its *manager's* bucket, so
            // the same application name installed from Homebrew on a Mac and from winget on a PC is
            // two summary rows with two different scripts — which is exactly the distinction the
            // OS-only grouping used to collapse (and, before per-manager buckets, silently handed
            // the Windows host a bash script over).
            var resolved = appGroup
                .Select(x => (Row: x, Path: ResolvePath(byNameAndPlatform, x.Name, x.OperatingSystem, PackageManagerOf(packageManagerNames, x.ParentApplicationId))))
                .Where(x => x.Path is not null);

            foreach (var platformGroup in resolved.GroupBy(x => x.Path!.Platform, x => x.Row, StringComparer.Ordinal))
            {
                var path = byNameAndPlatform[(appGroup.Key.ToLowerInvariant(), platformGroup.Key)];

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
                    // The row's own bucket — an OS one for an AI-researched row, its manager's for a
                    // package-manager-managed one. This round-trips back to the API (e.g. the
                    // Applications page's per-row instructions panel) as the platform that has to
                    // match the item PrepareUpgradePathScanQueryHandler builds for it.
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
            .Join(_context.Hosts, a => a.HostId, h => h.Id, (a, h) => new { a.HostId, a.Name, a.Version, a.ParentApplicationId, h.OperatingSystem })
            .ToListAsync(cancellationToken);

        var upgradePaths = await _context.UpgradePaths.ToListAsync(cancellationToken);
        var byNameAndPlatform = BuildByNameAndPlatformLookup(upgradePaths);
        var packageManagerNames = await LoadPackageManagerNamesAsync(installed.Select(x => x.ParentApplicationId), cancellationToken);

        var counts = new Dictionary<Guid, int>();

        foreach (var app in installed)
        {
            var path = ResolvePath(byNameAndPlatform, app.Name, app.OperatingSystem, PackageManagerOf(packageManagerNames, app.ParentApplicationId));
            if (path is null)
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

    /// <summary>
    /// Names of the package managers referenced by <paramref name="parentIds"/> — the applications
    /// other reported applications name as their manager. Resolved in one query rather than per
    /// row, and bounded by how many distinct package managers a fleet has (in practice: one or two).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> LoadPackageManagerNamesAsync(IEnumerable<Guid?> parentIds, CancellationToken cancellationToken)
    {
        var ids = parentIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await _context.InstalledApplications
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Name })
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);
    }

    private static string? PackageManagerOf(IReadOnlyDictionary<Guid, string> packageManagerNames, Guid? parentApplicationId) =>
        parentApplicationId.HasValue && packageManagerNames.TryGetValue(parentApplicationId.Value, out var name) ? name : null;

    /// <summary>
    /// The upgrade path one installed application resolves to, or null if none has been researched
    /// for it yet. Tries the host's own OS bucket first, then the bucket belonging to whichever
    /// package manager owns this installation.
    /// </summary>
    /// <remarks>
    /// The second lookup used to be a single shared <see cref="PlatformBucket.Generic"/> bucket,
    /// which only held Homebrew rows — so a Windows host with an application whose name happened to
    /// match a Homebrew formula would fall back onto a signed <c>#!/bin/bash</c> script and, since
    /// the signature is genuine, its agent would happily run it. Falling back to the *manager's*
    /// bucket instead means a row can only ever be inherited by an installation that manager
    /// actually manages. An application with no manager (<paramref name="packageManagerName"/> null)
    /// falls back to a bucket named after itself, which finds its own self-update row when the
    /// application is itself a package manager, and simply misses otherwise.
    /// </remarks>
    private static UpgradePath? ResolvePath(
        IReadOnlyDictionary<(string Name, string Platform), UpgradePath> byNameAndPlatform,
        string applicationName,
        string? operatingSystem,
        string? packageManagerName)
    {
        var name = applicationName.ToLowerInvariant();

        if (byNameAndPlatform.TryGetValue((name, PlatformBucket.From(operatingSystem)), out var path))
        {
            return path;
        }

        return byNameAndPlatform.TryGetValue((name, PlatformBucket.ForPackageManager(packageManagerName ?? applicationName)), out path)
            ? path
            : null;
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
