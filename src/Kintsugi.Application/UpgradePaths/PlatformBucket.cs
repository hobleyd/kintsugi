namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Buckets a host's free-text operating system string into one of a small set of platform
/// families, so an upgrade path researched once for one host can be reused by every other host
/// on the same platform, and so a package-manager command that runs the same way everywhere can
/// be stored once under <see cref="Generic"/> rather than duplicated per platform.
/// </summary>
public static class PlatformBucket
{
    public const string Generic = "generic";
    public const string MacOs = "macOS";

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
            return "Windows";
        }

        if (os.Contains("linux") || os.Contains("ubuntu") || os.Contains("debian") || os.Contains("centos") || os.Contains("fedora"))
        {
            return "Linux";
        }

        return Generic;
    }
}
