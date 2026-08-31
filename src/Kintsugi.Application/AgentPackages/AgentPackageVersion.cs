namespace Kintsugi.Application.AgentPackages;

/// <summary>
/// Comparing two agent build versions — "0.5.0" against "0.5.1" — to decide whether what's
/// upstream is actually newer than what this server has already published.
/// </summary>
public static class AgentPackageVersion
{
    /// <summary>
    /// True when <paramref name="candidate"/> should be offered as an upgrade over
    /// <paramref name="current"/>. Nothing published yet always counts as newer.
    ///
    /// Numeric comparison where both parse (so "0.10.0" beats "0.9.0", which an ordinal string
    /// compare gets backwards), falling back to "different means newer" otherwise. The fallback is
    /// deliberately permissive rather than silent: a version this can't parse is still worth
    /// surfacing on the Clients page, and importing it is a no-op if it turns out to be the same
    /// build — <c>ImportAgentPackagesFromSourceCommandHandler</c> skips a (platform, version) pair
    /// that's already published regardless of what this returns.
    /// </summary>
    public static bool IsNewer(string candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        if (Version.TryParse(candidate.Trim(), out var candidateVersion)
            && Version.TryParse(current.Trim(), out var currentVersion))
        {
            return candidateVersion > currentVersion;
        }

        return !string.Equals(candidate.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True only when <paramref name="candidate"/> is provably the higher of the two — both parse
    /// numerically and the candidate is strictly greater.
    ///
    /// Deliberately not <see cref="IsNewer"/>, which answers a different question. "Is there
    /// something different upstream worth showing on the page?" can afford to say yes when it
    /// can't tell; "which of these two is the higher version?" cannot. Asking the permissive one
    /// would make a pre-release tag beside its final release ("0.5.0-rc1" and "0.5.0") answer yes
    /// in *both* directions, so whichever GitHub happened to list second would win — the selected
    /// build would depend on listing order rather than on the versions.
    /// </summary>
    public static bool IsHigherThan(string candidate, string current) =>
        Version.TryParse(candidate.Trim(), out var candidateVersion)
        && Version.TryParse(current.Trim(), out var currentVersion)
        && candidateVersion > currentVersion;
}
