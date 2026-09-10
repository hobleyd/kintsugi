using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// The record that OSV was asked about one (ecosystem, source package, version) triple, when, and
/// what came back.
/// </summary>
/// <remarks>
/// <para>
/// The package-side sibling of <see cref="CpeAssessment"/>, and it deliberately has <b>no
/// mapping</b>. A CPE has to be confirmed by a human because "slack" could be Slackware; a
/// distribution's own source-package name has no such ambiguity — <c>openssl</c> on
/// <c>Ubuntu:22.04</c> means exactly one thing to Ubuntu's security team and to OSV alike. So
/// there is nothing to review, and packages start producing findings the moment they are
/// reported.
/// </para>
/// <para>
/// <b>Assessed against the distribution's advisories, never against NVD's version ranges.</b>
/// That is the entire reason this exists separately: distributions backport security fixes
/// without changing the upstream version, so a CPE match on the upstream version reports CVEs
/// that were fixed months ago. Verified against the live API — Ubuntu 22.04's openssl answers 48
/// vulnerabilities at <c>3.0.2-0ubuntu1.15</c> and 36 at <c>3.0.2-0ubuntu1.19</c>, the same
/// upstream 3.0.2 either way. Do not "unify" this with the CPE path.
/// </para>
/// <para>
/// Keyed on the triple rather than on a host, like <see cref="CpeAssessment"/> and for the same
/// two reasons: a hundred identical Ubuntu hosts share one answer, and the rows recording who has
/// it are rewritten on every inventory report.
/// </para>
/// </remarks>
public class PackageAssessment : BaseEntity
{
    /// <summary>The OSV ecosystem, e.g. <c>Ubuntu:22.04</c>, <c>Debian:12</c>,
    /// <c>Rocky Linux:9</c>. Composed by <c>OsvEcosystem.For</c> from the host's os-release
    /// facts; OSV rejects an unrecognized one with a 400 rather than an empty answer, which is
    /// what lets an unsupported distribution surface as a stated gap.</summary>
    public string Ecosystem { get; private set; } = default!;

    /// <summary>The source package name — see <see cref="InstalledPackage.Name"/>.</summary>
    public string Name { get; private set; } = default!;

    public string Version { get; private set; } = default!;

    /// <summary>When OSV last answered for this triple. Null on a row queued and not yet reached,
    /// which is what the background service takes first.</summary>
    public DateTimeOffset? LastAssessedUtc { get; private set; }

    public int MatchCount { get; private set; }

    public int KnownExploitedCount { get; private set; }

    /// <summary>Why the last attempt failed, when it did — most usefully OSV's own "invalid
    /// ecosystem", which is how a distribution it does not cover says so.</summary>
    public string? LastError { get; private set; }

    private PackageAssessment()
    {
    }

    public static PackageAssessment Queue(string ecosystem, string name, string version)
    {
        if (string.IsNullOrWhiteSpace(ecosystem))
        {
            throw new DomainException("A package assessment needs an ecosystem.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A package assessment needs a package name.");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new DomainException("A package assessment needs a version.");
        }

        return new PackageAssessment
        {
            Ecosystem = ecosystem.Trim(),
            Name = name.Trim(),
            Version = version.Trim()
        };
    }

    public void RecordAssessment(int matchCount, int knownExploitedCount)
    {
        MatchCount = matchCount;
        KnownExploitedCount = knownExploitedCount;
        LastAssessedUtc = DateTimeOffset.UtcNow;
        LastError = null;
        MarkUpdated();
    }

    /// <summary>Records a failure, stamping the cursor anyway — for the reason
    /// <see cref="CpeAssessment.RecordFailure"/> gives: a triple that fails every time must not
    /// park itself at the head of the queue and starve everything behind it.</summary>
    public void RecordFailure(string error)
    {
        LastError = string.IsNullOrWhiteSpace(error) ? "The assessment failed." : error.Trim();
        LastAssessedUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }
}

/// <summary>One CVE OSV says affects one (ecosystem, package, version) triple.</summary>
/// <remarks>
/// The package-side sibling of <see cref="VulnerabilityMatch"/>, separate because that one's
/// foreign key is a <see cref="CpeAssessment"/> and a package has none. Both point at the same
/// shared <see cref="Vulnerability"/> rows, which is what lets one CISA exploited flag light up a
/// finding whichever path found it.
/// </remarks>
public class PackageVulnerabilityMatch : BaseEntity
{
    public Guid PackageAssessmentId { get; private set; }

    public Guid VulnerabilityId { get; private set; }

    private PackageVulnerabilityMatch()
    {
    }

    public static PackageVulnerabilityMatch Create(Guid packageAssessmentId, Guid vulnerabilityId)
    {
        if (packageAssessmentId == Guid.Empty)
        {
            throw new DomainException("A package vulnerability match needs an assessment.");
        }

        if (vulnerabilityId == Guid.Empty)
        {
            throw new DomainException("A package vulnerability match needs a vulnerability.");
        }

        return new PackageVulnerabilityMatch
        {
            PackageAssessmentId = packageAssessmentId,
            VulnerabilityId = vulnerabilityId
        };
    }
}
