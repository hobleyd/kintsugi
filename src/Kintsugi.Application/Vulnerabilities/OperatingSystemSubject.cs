using System.Text.RegularExpressions;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Vulnerabilities;

/// <summary>
/// Turns a host's reported operating system into a stable CPE subject key and the version NVD
/// should be asked about.
/// </summary>
/// <remarks>
/// <para>
/// <b>This resolves an identity and a version, and deliberately not a vendor or a product.</b> It
/// would be easy to hardcode <c>macOS</c> → <c>apple:macos</c> and <c>Ubuntu</c> →
/// <c>canonical:ubuntu_linux</c>, and it would be wrong for the same reason it is wrong for
/// applications: NVD's product tokens are its own vocabulary, they change, and a table of guesses
/// in this file would be a second opinion nothing checks. An operating system goes through exactly
/// the same <see cref="Kintsugi.Domain.Entities.CpeMapping"/> queue an application does — proposed
/// by machine, confirmed by a human, validated against NVD's own dictionary.
/// </para>
/// <para>
/// <b>Coverage differs sharply by platform, and the screen says so.</b> macOS gives a clean
/// version straight from <c>sw_vers</c>. Windows needs the update revision — verified against the
/// live API, build 22631.4317 returns 1355 CVEs and 22631.6000 returns 793, so assuming revision
/// zero reports nearly every Windows CVE ever filed against a fully patched machine — and only an
/// agent new enough to send <c>Host.OperatingSystemVersionId</c> supplies it. Linux needs
/// os-release's <c>ID</c> and <c>VERSION_ID</c>, because <c>PRETTY_NAME</c> is prose. Where the
/// facts are missing this reports the subject as unassessable with a reason, rather than guessing
/// and presenting the guess as a finding.
/// </para>
/// <para>
/// One limit no agent release fixes: NVD's coverage of a Linux distribution as an *operating
/// system* is thin — <c>canonical:ubuntu_linux:24.04</c> answers 25 CVEs against macOS 14.5's
/// 1138 — because the real Linux vulnerability surface is per source package and lives in the
/// distributions' own trackers (Ubuntu USN/OVAL, Debian DSA). Kintsugi has no dpkg/rpm inventory
/// to join to those, deliberately (see src/CLAUDE.md on why apt and dnf are reported as OS updates
/// rather than as a package manager), so this is what is honestly available today.
/// </para>
/// </remarks>
public record OperatingSystemSubject(
    string SubjectKey,
    string DisplayName,
    string? Version,
    string? UnassessableReason)
{
    /// <summary>Whether NVD can actually be asked about this host's OS.</summary>
    public bool IsAssessable => Version is not null;

    private static readonly Regex MacOsVersion = new(@"\b(\d+(?:\.\d+){0,2})\b", RegexOptions.Compiled);
    private static readonly Regex WindowsRelease = new(@"\b(\d{2}H[12])\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WindowsFamily = new(@"\bWindows\s+(Server\s+\d{4}|\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WindowsBuild = new(@"\((\d{4,6})\)", RegexOptions.Compiled);

    /// <summary>
    /// Derives the subject for <paramref name="host"/>, or null when it has reported no operating
    /// system at all — a host that has enrolled and never checked in.
    /// </summary>
    public static OperatingSystemSubject? Derive(Host host) =>
        Derive(host.OperatingSystem, host.OperatingSystemId, host.OperatingSystemVersionId);

    /// <summary>The pure half, so the platform rules can be pinned by tests without a Host.</summary>
    public static OperatingSystemSubject? Derive(string? operatingSystem, string? operatingSystemId, string? operatingSystemVersionId)
    {
        var reported = operatingSystem?.Trim();
        if (string.IsNullOrEmpty(reported))
        {
            return null;
        }

        // Checked before the name is inspected: an agent that sends os-release's own ID has told
        // us exactly what this is, and that beats anything inferred from prose. This is the Linux
        // path in practice, but nothing here is Linux-specific — if a future agent reports an ID
        // for another platform, it wins there too.
        if (!string.IsNullOrWhiteSpace(operatingSystemId))
        {
            var key = Normalize(operatingSystemId);
            var version = string.IsNullOrWhiteSpace(operatingSystemVersionId) ? null : operatingSystemVersionId.Trim();

            return new OperatingSystemSubject(
                key,
                reported,
                version,
                version is null
                    ? "The agent reported this distribution's identifier but no VERSION_ID, so there is no version to assess against."
                    : null);
        }

        if (reported.Contains("macOS", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase))
        {
            return DeriveMacOs(reported);
        }

        if (reported.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return DeriveWindows(reported, operatingSystemVersionId);
        }

        // Anything else — a Linux host running an agent that predates os-release reporting, or a
        // platform nothing here knows. Given a key so it appears on the mapping screen with its
        // reason, rather than being dropped and leaving the fleet looking fully covered.
        return new OperatingSystemSubject(
            Normalize(reported),
            reported,
            null,
            "This host's agent does not report the operating system's machine-readable identifier and version. "
            + "Upgrade the agent on this host to assess its operating system.");
    }

    private static OperatingSystemSubject DeriveMacOs(string reported)
    {
        // "macOS 14.5" / "macOS 26.1" — sw_vers -productName and -productVersion, joined by the
        // agent's system_info::operating_system. The version is whatever numeric token is present.
        var match = MacOsVersion.Match(reported);

        return new OperatingSystemSubject(
            "macos",
            "macOS",
            match.Success ? match.Groups[1].Value : null,
            match.Success ? null : $"Could not read a macOS version out of '{reported}'.");
    }

    private static OperatingSystemSubject DeriveWindows(string reported, string? operatingSystemVersionId)
    {
        // "Windows 11 Pro 23H2 (22631)" — see the Windows agent's system_info::operating_system,
        // which reads ProductName, DisplayVersion and CurrentBuildNumber out of the registry and
        // corrects Windows 11's ProductName, which still says "Windows 10".
        var family = WindowsFamily.Match(reported);
        var release = WindowsRelease.Match(reported);

        var familyToken = family.Success ? Normalize(family.Groups[1].Value) : "unknown";
        var releaseToken = release.Success ? release.Groups[1].Value.ToUpperInvariant() : null;

        // The edition ("Pro", "Enterprise") is deliberately dropped: NVD indexes Windows by
        // family and feature update, not by SKU, so keeping it would split one mapping into as
        // many rows as the fleet has editions and ask a human to confirm each identically.
        var subjectKey = releaseToken is null ? $"windows_{familyToken}" : $"windows_{familyToken}_{releaseToken.ToLowerInvariant()}";
        var displayName = releaseToken is null
            ? $"Windows {(family.Success ? family.Groups[1].Value : "(unrecognized)")}"
            : $"Windows {family.Groups[1].Value} {releaseToken}";

        // Preferred: the agent's own build-with-revision, e.g. "22631.4317". This is the only
        // thing that makes the answer honest — see the class remarks.
        if (!string.IsNullOrWhiteSpace(operatingSystemVersionId))
        {
            var reportedBuild = operatingSystemVersionId.Trim();
            var version = reportedBuild.StartsWith("10.0.", StringComparison.Ordinal) ? reportedBuild : $"10.0.{reportedBuild}";
            return new OperatingSystemSubject(subjectKey, displayName, version, null);
        }

        var build = WindowsBuild.Match(reported);
        if (!build.Success)
        {
            return new OperatingSystemSubject(subjectKey, displayName, null, $"Could not read a Windows build number out of '{reported}'.");
        }

        // Refused rather than approximated. Querying NVD for 10.0.<build>.0 returns every CVE
        // fixed in any revision of that build — on a fully patched machine, hundreds of findings
        // that are all wrong. A wrong answer here is worse than no answer, because it is the
        // answer an administrator would act on.
        return new OperatingSystemSubject(
            subjectKey,
            displayName,
            null,
            $"This host's agent reports the Windows build ({build.Groups[1].Value}) without its update revision, "
            + "and NVD matches Windows CVEs on the revision. Upgrade the agent on this host to assess its operating system.");
    }

    /// <summary>Folds a name to the same shape <c>CpeMapping.SubjectKey</c> holds: lower case,
    /// with runs of anything that is not a letter or digit collapsed to a single underscore.</summary>
    private static string Normalize(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
}
