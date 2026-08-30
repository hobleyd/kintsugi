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
    // The app bundle's CFBundleIdentifier, when this variant was sourced from a scanned macOS app
    // bundle. Null for non-bundle sources (e.g. Homebrew) and for non-macOS platforms.
    string? ApplicationIdentifier = null);
