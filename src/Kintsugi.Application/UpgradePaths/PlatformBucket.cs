namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Buckets a host's free-text operating system string into one of a small set of platform
/// families, so an upgrade path researched once for one host can be reused by every other host
/// on the same platform.
/// </summary>
/// <remarks>
/// A package-manager-managed application doesn't live under an OS bucket at all — it lives under
/// its <em>manager's</em> own bucket (see <see cref="ForPackageManager"/>), because what a
/// `brew upgrade` / `winget upgrade` row actually depends on is the manager, not the OS. That used
/// to be one shared "generic" bucket, which was safe only while Homebrew was the sole package
/// manager in existence here: the lookup in <c>UpgradePathRepository</c> falls back to it for
/// <em>any</em> host, so a Windows host with an application whose name happened to match a
/// Homebrew formula would have been handed a signed `#!/bin/bash` script and run it. Keying by
/// manager keeps a macOS Homebrew row, a Windows winget row, and a Windows Chocolatey row for the
/// same application name as three separate, non-substitutable rows.
/// </remarks>
public static class PlatformBucket
{
    /// <summary>An OS this system doesn't recognize — still a real bucket (its own AI-researched
    /// rows), just not one anything else is keyed off. Retained as the historical bucket every
    /// pre-<see cref="ForPackageManager"/> Homebrew row was stored under; the
    /// <c>SplitPackageManagerPlatformBucket</c> migration moves those to <c>pm:Homebrew</c>.</summary>
    public const string Generic = "generic";
    public const string MacOs = "macOS";
    public const string Windows = "Windows";
    public const string Linux = "Linux";

    /// <summary>Prefix marking a bucket as a package manager's rather than an operating system's —
    /// short because <c>upgrade_paths.Platform</c> is capped at 32 characters (see
    /// <c>UpgradePathConfiguration</c>).</summary>
    private const string PackageManagerPrefix = "pm:";

    public static string From(string? operatingSystem)
    {
        if (string.IsNullOrWhiteSpace(operatingSystem))
        {
            return Generic;
        }

        var os = operatingSystem.ToLowerInvariant();

        if (os.Contains("mac") || os.Contains("darwin"))
        {
            return MacOs;
        }

        if (os.Contains("windows"))
        {
            return Windows;
        }

        if (os.Contains("linux") || os.Contains("ubuntu") || os.Contains("debian") || os.Contains("centos") || os.Contains("fedora"))
        {
            return Linux;
        }

        return Generic;
    }

    /// <summary>
    /// The bucket every application managed by <paramref name="packageManagerName"/> is stored
    /// under — including that manager's own self-update row, since a manager is trivially its own
    /// manager. Normalized to the catalog's canonical casing where the manager is a recognized one
    /// (see <see cref="PackageManagerCatalog"/>), so a host reporting "winget" and another
    /// reporting "WinGet" resolve to the same row rather than two.
    /// </summary>
    public static string ForPackageManager(string packageManagerName) =>
        PackageManagerPrefix + PackageManagerCatalog.Canonicalize(packageManagerName);

    public static bool IsPackageManagerBucket(string platform) =>
        platform.StartsWith(PackageManagerPrefix, StringComparison.Ordinal);

    /// <summary>The manager name back out of a <see cref="ForPackageManager"/> bucket, or null if
    /// <paramref name="platform"/> is an OS bucket.</summary>
    public static string? PackageManagerNameFrom(string platform) =>
        IsPackageManagerBucket(platform) ? platform[PackageManagerPrefix.Length..] : null;
}
