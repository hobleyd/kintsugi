namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Queries OSV.dev for the vulnerabilities affecting distribution packages.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because NVD cannot answer the question for a Linux package.</b> Distributions
/// backport security fixes without changing the upstream version, so matching a dpkg version
/// against NVD's CPE ranges reports CVEs the distribution fixed months ago. OSV carries the
/// distributions' own advisories and is therefore backport-aware: verified against the live API,
/// Ubuntu 22.04's <c>openssl</c> answers 48 vulnerabilities at <c>3.0.2-0ubuntu1.15</c> and 36 at
/// <c>3.0.2-0ubuntu1.19</c> — the same upstream 3.0.2 either way. Do not route packages through
/// <c>INvdClient</c>.
/// </para>
/// <para>
/// <b>Query by name and ecosystem, never by purl.</b> Both forms are documented and only one
/// works everywhere: <c>pkg:rpm/rocky/openssl@3.0.7-27.el9</c> answers nothing while
/// <c>{name: openssl, ecosystem: "Rocky Linux:9"}</c> answers ten.
/// </para>
/// <para>
/// <b>And the name is the source package.</b> <c>libssl3</c> answers 0 on Ubuntu 22.04 where its
/// source package <c>openssl</c> answers 48; <c>libc6</c> answers 0 where <c>glibc</c> answers
/// 38. The agents report source names for this reason — see <c>InstalledPackage.Name</c>.
/// </para>
/// </remarks>
public interface IOsvClient
{
    /// <summary>
    /// Asks about many packages at once, returning the OSV identifiers affecting each.
    /// </summary>
    /// <remarks>
    /// One HTTP call per batch rather than per package, which is what makes assessing a few
    /// thousand distinct triples cheap. OSV publishes no API key and no rate limit, but the
    /// caller still bounds itself per run — see <c>VulnerabilitySettings.AssessmentsPerRun</c> —
    /// because a first run against a whole fleet should not be one enormous request either.
    ///
    /// A query whose ecosystem OSV does not recognize fails the <em>whole batch</em> with a 400,
    /// so an implementation must not mix ecosystems it has not validated; the caller batches per
    /// ecosystem for that reason.
    /// </remarks>
    Task<IReadOnlyList<OsvQueryResult>> QueryBatchAsync(
        IReadOnlyList<OsvPackageQuery> queries, CancellationToken cancellationToken);

    /// <summary>
    /// The CVE identifiers one OSV record stands for.
    /// </summary>
    /// <remarks>
    /// Needed only for identifiers that do not name their CVE themselves — the RHEL family's
    /// <c>RLSA-2022:7288</c> stands for two CVEs and says so nowhere in its name, while
    /// <c>UBUNTU-CVE-2024-2511</c> needs no lookup at all (see
    /// <c>OsvAdvisory.CveFromIdentifier</c>). The answer is cached in <c>osv_advisories</c>,
    /// because it never changes.
    ///
    /// Reads OSV's <c>upstream</c> and <c>aliases</c> both: every record checked carried its CVEs
    /// in <c>upstream</c> and none in <c>aliases</c>, but <c>aliases</c> is the older and more
    /// widely populated field and other ecosystems use it.
    /// </remarks>
    Task<IReadOnlyList<string>> GetCveIdsAsync(string osvId, CancellationToken cancellationToken);
}

public record OsvPackageQuery(string Ecosystem, string Name, string Version);

/// <param name="OsvIds">Empty when nothing affects this package at this version — which for a
/// patched host is the common and correct answer.</param>
public record OsvQueryResult(OsvPackageQuery Query, IReadOnlyList<string> OsvIds);
