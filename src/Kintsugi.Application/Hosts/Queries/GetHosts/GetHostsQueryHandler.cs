using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Hosts.Queries.GetHosts;

public class GetHostsQueryHandler : IRequestHandler<GetHostsQuery, IReadOnlyList<HostDto>>
{
    private readonly IHostRepository _hostRepository;
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IVulnerabilityRepository _vulnerabilityRepository;
    private readonly IInstalledPackageRepository _installedPackageRepository;

    public GetHostsQueryHandler(
        IHostRepository hostRepository,
        IUpgradePathRepository upgradePathRepository,
        IVulnerabilityRepository vulnerabilityRepository,
        IInstalledPackageRepository installedPackageRepository)
    {
        _hostRepository = hostRepository;
        _upgradePathRepository = upgradePathRepository;
        _vulnerabilityRepository = vulnerabilityRepository;
        _installedPackageRepository = installedPackageRepository;
    }

    public async Task<IReadOnlyList<HostDto>> Handle(GetHostsQuery request, CancellationToken cancellationToken)
    {
        var hosts = await _hostRepository.GetAllAsync(cancellationToken);

        var patchStatuses = await _upgradePathRepository.GetInstallationPatchStatusesAsync(cancellationToken);
        var appCveMatches = await _vulnerabilityRepository.GetApplicationCveMatchesAsync(cancellationToken);
        var osCveMatches = await _vulnerabilityRepository.GetOperatingSystemCveMatchesAsync(cancellationToken);
        var packageCveMatches = await _vulnerabilityRepository.GetPackageCveMatchesAsync(cancellationToken);

        var appUpdateCounts = BuildAppUpdateCountsByHost(patchStatuses);

        var unpatched = new Dictionary<Guid, HashSet<string>>();
        var patched = new Dictionary<Guid, HashSet<string>>();

        AddApplicationCveCounts(patchStatuses, appCveMatches, unpatched, patched);
        AddOperatingSystemCveCounts(hosts, osCveMatches, unpatched, patched);
        await AddPackageCveCountsAsync(hosts, packageCveMatches, unpatched, patched, cancellationToken);

        return hosts
            .Select(host => HostDto.FromEntity(
                host,
                appUpdateCounts.GetValueOrDefault(host.Id),
                unpatched.GetValueOrDefault(host.Id)?.Count ?? 0,
                patched.GetValueOrDefault(host.Id)?.Count ?? 0))
            .ToList();
    }

    private static IReadOnlyDictionary<Guid, int> BuildAppUpdateCountsByHost(
        IReadOnlyList<InstallationPatchStatus> patchStatuses)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var status in patchStatuses.Where(s => s.UpdateAvailable == true))
        {
            counts[status.HostId] = counts.GetValueOrDefault(status.HostId) + 1;
        }

        return counts;
    }

    /// <summary>
    /// Folds each host's application CVE matches into the unpatched or patched bucket. Anything
    /// short of a confirmed-current installation counts as unpatched: an installation with no
    /// resolved upgrade path (<see cref="InstallationPatchStatus.UpdateAvailable"/> is null) has
    /// no verdict that it is fixed, so a CVE against it stays live rather than being dropped as a
    /// coverage gap.
    /// </summary>
    private static void AddApplicationCveCounts(
        IReadOnlyList<InstallationPatchStatus> patchStatuses,
        IReadOnlyList<ApplicationCveMatch> cveMatches,
        Dictionary<Guid, HashSet<string>> unpatched,
        Dictionary<Guid, HashSet<string>> patched)
    {
        var cveIdsBySubject = cveMatches
            .GroupBy(m => (m.SubjectKey, m.Version))
            .ToDictionary(g => g.Key, g => g.Select(m => m.CveId).ToList());

        foreach (var status in patchStatuses)
        {
            if (!cveIdsBySubject.TryGetValue((status.ApplicationName.ToLowerInvariant(), status.Version), out var cveIds))
            {
                continue;
            }

            var bucket = status.UpdateAvailable == false ? patched : unpatched;
            UnionInto(bucket, status.HostId, cveIds);
        }
    }

    /// <summary>
    /// The operating-system sibling of <see cref="AddApplicationCveCounts"/>. There is no
    /// per-installation verdict for an OS the way <see cref="InstallationPatchStatus"/> gives one
    /// for an application, so the split uses <see cref="Host.OperatingSystemUpdateAvailable"/>
    /// directly — unknown (null, never checked) reads as unpatched, the same rule an unresolved
    /// application path gets, since nothing has confirmed the host is current.
    /// </summary>
    private static void AddOperatingSystemCveCounts(
        IReadOnlyList<Host> hosts,
        IReadOnlyList<OperatingSystemCveMatch> cveMatches,
        Dictionary<Guid, HashSet<string>> unpatched,
        Dictionary<Guid, HashSet<string>> patched)
    {
        var cveIdsBySubject = cveMatches
            .GroupBy(m => (m.SubjectKey, m.Version))
            .ToDictionary(g => g.Key, g => g.Select(m => m.CveId).ToList());

        foreach (var host in hosts)
        {
            var derived = OperatingSystemSubject.Derive(host);
            if (derived?.Version is null)
            {
                // Never checked in, or reports too little to assess (a Windows build with no
                // update revision, say) — a coverage gap, not a clean bill of health, so it
                // contributes to neither bucket rather than being guessed at.
                continue;
            }

            if (!cveIdsBySubject.TryGetValue((derived.SubjectKey, derived.Version), out var cveIds))
            {
                continue;
            }

            var bucket = host.OperatingSystemUpdateAvailable == false ? patched : unpatched;
            UnionInto(bucket, host.Id, cveIds);
        }
    }

    /// <summary>
    /// The package sibling of <see cref="AddApplicationCveCounts"/>. A distribution package has
    /// no upgrade path or latest-version of its own — apt, dnf, zypper and pacman are deliberately
    /// outside <c>PackageManagerCatalog</c> (see src/CLAUDE.md) — but that does not mean no verdict
    /// exists: apt/dnf patches the operating system *and every package it shipped* in one pull, the
    /// same way <c>softwareupdate</c> does on macOS, so <see cref="Host.OperatingSystemUpdateAvailable"/>
    /// already answers "is this package current" as surely as it answers that question for the OS
    /// itself. Reusing it here is what stops a fully-patched Linux fleet — one whose distribution
    /// simply has not backported a fix yet — from reading as entirely unpatched.
    /// </summary>
    private async Task AddPackageCveCountsAsync(
        IReadOnlyList<Host> hosts,
        IReadOnlyList<PackageCveMatch> cveMatches,
        Dictionary<Guid, HashSet<string>> unpatched,
        Dictionary<Guid, HashSet<string>> patched,
        CancellationToken cancellationToken)
    {
        if (cveMatches.Count == 0)
        {
            return;
        }

        var osUpdateAvailableByHost = hosts.ToDictionary(h => h.Id, h => h.OperatingSystemUpdateAvailable);

        var cveIdsByTriple = cveMatches
            .GroupBy(m => new PackageTriple(m.Ecosystem, m.Name, m.Version))
            .ToDictionary(g => g.Key, g => g.Select(m => m.CveId).ToList());

        var names = cveMatches.Select(m => m.Name).Distinct().ToList();
        var versions = cveMatches.Select(m => m.Version).Distinct().ToList();

        var hostPackages = await _installedPackageRepository.GetHostPackageTriplesAsync(names, versions, cancellationToken);

        foreach (var hostPackage in hostPackages)
        {
            if (!cveIdsByTriple.TryGetValue(hostPackage.Triple, out var cveIds))
            {
                continue;
            }

            var bucket = osUpdateAvailableByHost.GetValueOrDefault(hostPackage.HostId) == false ? patched : unpatched;
            UnionInto(bucket, hostPackage.HostId, cveIds);
        }
    }

    private static void UnionInto(Dictionary<Guid, HashSet<string>> bucket, Guid hostId, IReadOnlyList<string> cveIds)
    {
        if (!bucket.TryGetValue(hostId, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bucket[hostId] = set;
        }

        set.UnionWith(cveIds);
    }
}
