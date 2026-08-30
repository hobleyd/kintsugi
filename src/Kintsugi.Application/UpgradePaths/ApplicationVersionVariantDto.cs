namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// One distinct (application, version, operating system, parent application) combination seen
/// across the fleet — not one row per host. A large fleet can have a huge number of (host,
/// application) installations but a small, bounded number of distinct version/OS combinations, so
/// grouping down to this shape at the database level is what keeps upgrade-path planning cheap
/// regardless of fleet size.
/// </summary>
public record ApplicationVersionVariantDto(
    string ApplicationName,
    string? ParentApplicationName,
    string? OperatingSystem,
    string Version,
    // Whatever stably names this application on its platform: a macOS app bundle's
    // CFBundleIdentifier, a Windows application's uninstall-registry key name, or a winget /
    // Chocolatey package id. Null when the source has no identifier separate from the name (e.g. a
    // Homebrew formula).
    string? ApplicationIdentifier = null);
