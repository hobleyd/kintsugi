namespace Kintsugi.Application.Vulnerabilities;

/// <summary>
/// Turns a Linux host's os-release facts into the ecosystem string OSV indexes its
/// distribution advisories under.
/// </summary>
/// <remarks>
/// <para>
/// A small table of mappings, and the risk with any such table is that a wrong string looks
/// exactly like a clean host. It does not here, and that is worth knowing before changing any of
/// it: <b>OSV validates the ecosystem and answers an unrecognized one with HTTP 400 "invalid
/// ecosystem"</b>, not with an empty result. Verified against the live API — <c>Fedora:40</c>
/// 400s while <c>Ubuntu:24.04</c>, <c>Debian:11</c>, <c>AlmaLinux:8</c>, <c>Rocky Linux:9</c>,
/// <c>Red Hat:9</c>, <c>SUSE:15.6</c> and <c>openSUSE:Leap:15.6</c> all answer 200. So a typo
/// here surfaces on the Vulnerabilities screen as a failed assessment naming the ecosystem, and
/// a distribution OSV does not cover says so in its own words.
/// </para>
/// <para>
/// The version part is not uniform, which is the other thing to get right. Ubuntu keys on the
/// full <c>VERSION_ID</c> (<c>24.04</c>); Debian on the major alone (<c>12</c>, and its
/// <c>VERSION_ID</c> is already just that); the RHEL family on the major of a <c>9.4</c>-style
/// <c>VERSION_ID</c>; Alpine on <c>v</c> plus major.minor.
/// </para>
/// </remarks>
public static class OsvEcosystem
{
    /// <summary>
    /// The OSV ecosystem for a host, or null when its distribution has no known mapping — which
    /// the caller reports as packages collected but not assessed, with the reason, rather than as
    /// a host with nothing wrong with it.
    /// </summary>
    /// <param name="operatingSystemId">os-release's <c>ID</c>, e.g. <c>ubuntu</c>.</param>
    /// <param name="operatingSystemVersionId">os-release's <c>VERSION_ID</c>, e.g. <c>24.04</c>.</param>
    public static string? For(string? operatingSystemId, string? operatingSystemVersionId)
    {
        if (string.IsNullOrWhiteSpace(operatingSystemId) || string.IsNullOrWhiteSpace(operatingSystemVersionId))
        {
            return null;
        }

        var id = operatingSystemId.Trim().ToLowerInvariant();
        var version = operatingSystemVersionId.Trim();

        return id switch
        {
            "ubuntu" => $"Ubuntu:{version}",
            // Debian's VERSION_ID is already the major ("12"), but a point release is taken to
            // the major anyway rather than trusting that to stay true.
            "debian" => $"Debian:{Major(version)}",
            "rocky" => $"Rocky Linux:{Major(version)}",
            "almalinux" => $"AlmaLinux:{Major(version)}",
            // RHEL and its rebuilds that track it exactly. CentOS Stream is deliberately absent:
            // it runs *ahead* of RHEL, so answering it with Red Hat's advisories would report
            // fixes as missing that Stream shipped first.
            "rhel" or "redhat" => $"Red Hat:{Major(version)}",
            "sles" or "sled" => $"SUSE:{version}",
            "opensuse-leap" => $"openSUSE:Leap:{version}",
            "alpine" => $"Alpine:v{MajorMinor(version)}",
            _ => null
        };
    }

    /// <summary>What to tell an administrator when <see cref="For"/> returns null.</summary>
    public static string UnsupportedReason(string? operatingSystemId, string? operatingSystemVersionId)
    {
        if (string.IsNullOrWhiteSpace(operatingSystemId) || string.IsNullOrWhiteSpace(operatingSystemVersionId))
        {
            return "This host's agent does not report the distribution identifier and version that a package "
                + "assessment needs. Upgrade the agent on this host.";
        }

        return $"No vulnerability database covers '{operatingSystemId.Trim()} {operatingSystemVersionId.Trim()}' "
            + "at the package level, so this host's packages are recorded but not assessed. Fedora is the "
            + "common case — OSV carries no Fedora advisories.";
    }

    private static string Major(string version) => version.Split('.')[0];

    private static string MajorMinor(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
    }
}
