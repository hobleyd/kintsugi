namespace Kintsugi.Application.UpgradePaths;

public enum UpgradePathWorkKind
{
    /// <summary>Research via the AI agent.</summary>
    Research,

    /// <summary>Managed by another tracked application (e.g. a Homebrew formula/cask) —
    /// <see cref="UpgradePathWorkItem.PackageManagerName"/> names that manager.</summary>
    PackageManagerManaged,

    /// <summary>The application is itself a tracked package manager — its own upgrade path is
    /// that manager's self-update command.</summary>
    PackageManagerSelfUpdate
}

/// <summary>One (application, platform) combination a scan needs to resolve.</summary>
public record UpgradePathWorkItem(
    string ApplicationName,
    string Platform,
    IReadOnlyList<string> KnownVersions,
    UpgradePathWorkKind Kind,
    string? PackageManagerName,
    // The app bundle's CFBundleIdentifier, when known — a disambiguating search signal handed to
    // the AI researcher. Only ever populated for the macOS platform bucket.
    string? ApplicationIdentifier = null);
