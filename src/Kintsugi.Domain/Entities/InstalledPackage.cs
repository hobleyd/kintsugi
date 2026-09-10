using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One operating-system package installed on a Linux host — a dpkg or rpm entry, recorded for
/// vulnerability assessment and for nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own table rather than a flag on <see cref="InstalledApplication"/>, and that is the
/// whole design.</b> These must never appear on the Applications screen, must never grow an
/// <see cref="UpgradePath"/>, must never be offered to the AI and must never be patched. That
/// list of "must never"s is enforced by `installed_applications` being a different table read by
/// `GetApplicationSummaries`, `UpgradePathRepository`, `VantaResourceBuilder`, the patch cycles
/// and `is_patchable` — none of which can see this one. A boolean column would have meant
/// auditing every one of those call sites and hoping a future one remembered.
/// </para>
/// <para>
/// The decision that keeps apt and dnf out of <c>PackageManagerCatalog</c> is untouched by this
/// and still holds: it is about <em>patchability</em> — "the latest version of curl" depends on
/// which repositories a host has configured, so one shared row would have Debian and Ubuntu
/// overwriting each other's answer forever. Reporting what is installed so it can be *assessed*
/// asks nothing of that catalogue. See src/CLAUDE.md.
/// </para>
/// <para>
/// <b><see cref="Name"/> is the <em>source</em> package, not the binary one, and that is not a
/// detail.</b> Distributions track CVEs against source packages, and so does OSV: verified
/// against the live API, <c>libssl3</c> answers 0 vulnerabilities on Ubuntu 22.04 while its
/// source package <c>openssl</c> answers 48, and <c>libc6</c> answers 0 while <c>glibc</c>
/// answers 38. Reporting binary names would silently under-report almost everything — the exact
/// failure this feature exists to fix. The Linux agent therefore reads
/// <c>${source:Package}</c>/<c>${source:Version}</c> from dpkg and derives the source name from
/// <c>%{SOURCERPM}</c> for rpm.
/// </para>
/// </remarks>
public class InstalledPackage : BaseEntity
{
    public Guid HostId { get; private set; }

    /// <summary>The <em>source</em> package name, e.g. <c>openssl</c>, <c>glibc</c>. See the class
    /// remarks for why this is not the binary package name.</summary>
    public string Name { get; private set; } = default!;

    /// <summary>The distribution's own version string, e.g. <c>3.0.2-0ubuntu1.19</c> — including
    /// its packaging revision, which is what carries a backported security fix. Compared by OSV
    /// against the distribution's own advisories, never against an upstream version range.</summary>
    public string Version { get; private set; } = default!;

    /// <summary>
    /// Which packaging system reported it — <c>dpkg</c> or <c>rpm</c>. Recorded for display and
    /// for diagnosing an agent that reported the wrong thing; the OSV ecosystem is derived from
    /// the host's own distribution rather than from this.
    /// </summary>
    public string Source { get; private set; } = default!;

    private InstalledPackage()
    {
    }

    public InstalledPackage(Guid hostId, string name, string version, string source)
    {
        if (hostId == Guid.Empty)
        {
            throw new DomainException("HostId is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Package name is required.");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new DomainException("Package version is required.");
        }

        HostId = hostId;
        Name = name.Trim();
        Version = version.Trim();
        Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim().ToLowerInvariant();
    }
}
