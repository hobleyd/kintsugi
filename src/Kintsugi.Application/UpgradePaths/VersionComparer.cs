using System.Text.RegularExpressions;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Lenient version comparison across the inconsistent version schemes real-world installers use
/// (dotted numerics, build suffixes, year-based releases). Not a full semver parser — just a
/// numeric-component comparison with a same-string short-circuit, biased towards reporting a
/// possible update rather than silently hiding one it can't confidently parse.
/// </summary>
public static class VersionComparer
{
    public static bool IsNewer(string? latest, string installed)
    {
        if (string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        if (string.Equals(latest.Trim(), installed.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var latestParts = ExtractNumbers(latest);
        var installedParts = ExtractNumbers(installed);

        if (latestParts.Count == 0 || installedParts.Count == 0)
        {
            return true;
        }

        for (var i = 0; i < Math.Max(latestParts.Count, installedParts.Count); i++)
        {
            var latestPart = i < latestParts.Count ? latestParts[i] : 0;
            var installedPart = i < installedParts.Count ? installedParts[i] : 0;

            if (latestPart != installedPart)
            {
                return latestPart > installedPart;
            }
        }

        return false;
    }

    // Matches only the first contiguous dotted/hyphenated numeric run (e.g. the "2026.1.3" in
    // "2026.1.3 Patch 1" or "2026.1.3,1"), not every digit in the string. Trailing qualifiers
    // like "Patch 1", "Beta 2", or a Homebrew cask's ",<revision>" suffix are free text appended
    // by the vendor or package manager, not real version components — treating their digits as
    // one caused "2026.1.3 Patch 1" to compare as newer than the identical "2026.1.3".
    private static List<int> ExtractNumbers(string version)
    {
        var match = Regex.Match(version, @"\d+(?:[.\-]\d+)*");
        return match.Success
            ? match.Value.Split('.', '-').Select(part => int.TryParse(part, out var n) ? n : 0).ToList()
            : new List<int>();
    }
}
