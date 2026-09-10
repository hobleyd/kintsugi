using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// The Linux operating-system package inventory.
/// </summary>
/// <remarks>
/// Separate from <see cref="IInstalledApplicationRepository"/> so that nothing reading
/// applications can accidentally read packages. That is the enforcement mechanism for "these
/// never appear on the Applications screen" — see <see cref="InstalledPackage"/>.
/// </remarks>
public interface IInstalledPackageRepository
{
    /// <summary>Replaces a host's whole package list. Agents report a full snapshot, so a
    /// package that has been removed disappears by not being in the new one.</summary>
    Task ReplaceForHostAsync(Guid hostId, IEnumerable<InstalledPackage> packages, CancellationToken cancellationToken);

    /// <summary>
    /// Every distinct (ecosystem, source package, version) triple installed across live hosts,
    /// with the ecosystem resolved per host from its os-release facts.
    /// </summary>
    /// <remarks>
    /// The ecosystem is a property of the *host*, not of the package row, so this join is what
    /// turns "openssl 3.0.2-0ubuntu1.19 on four machines" into one question for OSV. Hosts whose
    /// distribution has no OSV ecosystem are excluded here and counted separately — see
    /// <see cref="GetUnsupportedPackageHostCountAsync"/>.
    /// </remarks>
    Task<IReadOnlyList<PackageTriple>> GetDistinctPackageTriplesAsync(CancellationToken cancellationToken);

    /// <summary>How many live hosts have reported packages that cannot be assessed, because
    /// their distribution has no OSV ecosystem. A stated gap on the screen rather than an
    /// absence.</summary>
    Task<int> GetUnsupportedPackageHostCountAsync(CancellationToken cancellationToken);

    /// <summary>How many live hosts run one (ecosystem, package, version) triple — what sizes a
    /// finding without loading the whole cross product.</summary>
    Task<IReadOnlyDictionary<PackageTriple, int>> GetHostCountsByTripleAsync(CancellationToken cancellationToken);

    /// <summary>The ids of live hosts running any of these triples. Ids rather than a count,
    /// so a host exposed by both a package and an application is counted once.</summary>
    Task<IReadOnlyList<Guid>> GetHostIdsForTriplesAsync(
        IReadOnlyCollection<PackageTriple> triples, CancellationToken cancellationToken);

    /// <summary>The hostnames running one triple, loaded only when a finding is expanded.</summary>
    Task<IReadOnlyList<string>> GetHostnamesForTripleAsync(PackageTriple triple, CancellationToken cancellationToken);
}

/// <param name="Ecosystem">The OSV ecosystem, resolved from the host's os-release facts by
/// <c>OsvEcosystem.For</c> — e.g. <c>Ubuntu:24.04</c>.</param>
/// <param name="Name">The source package name.</param>
public record PackageTriple(string Ecosystem, string Name, string Version);
