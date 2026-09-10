using System.Globalization;

namespace Kintsugi.Application.Vulnerabilities;

/// <summary>
/// Computes a CVSS base score from its vector string.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is arithmetic, not estimation.</b> A CVSS base score is a pure function of its vector,
/// specified exactly by FIRST (https://www.first.org/cvss/v3.1/specification-document), so the
/// number computed here for a given vector is the number NVD publishes for that same vector.
/// That is what makes deriving one honest where inventing a severity would not be — compare
/// <c>VantaResourceBuilder</c>, which refuses to derive a severity precisely because staleness has
/// no such formula behind it.
/// </para>
/// <para>
/// <b>It exists because the Linux package path has vectors but no scores.</b> OSV answers "this
/// version is affected" and carries the advisory's own CVSS vector, while the base score is
/// something NVD publishes. Without this, a CVE found only through a distribution package — which
/// on a Linux fleet is most of them — would sit on the Vulnerabilities screen as "Unscored"
/// forever, unsortable and unbandable, even though everything needed to score it had already been
/// downloaded.
/// </para>
/// <para>
/// <b>What it will not do.</b> Only CVSS v3.0 and v3.1 base vectors are computed. v4.0 scores
/// through a MacroVector lookup table that is a different piece of work, and v2 is a different
/// formula again; for either, the vector is stored and the score left null, which the screen shows
/// as unscored rather than as zero. A vector that does not parse is refused rather than
/// part-computed — a score built from defaults would look exactly like a real one.
/// </para>
/// </remarks>
public static class CvssVector
{
    /// <summary>
    /// The base score and severity band for <paramref name="vector"/>, or null when it is not a
    /// v3.x base vector or does not parse.
    /// </summary>
    public static CvssScore? Score(string? vector)
    {
        if (string.IsNullOrWhiteSpace(vector))
        {
            return null;
        }

        var trimmed = vector.Trim();
        var version = trimmed.StartsWith("CVSS:3.1/", StringComparison.OrdinalIgnoreCase) ? "3.1"
            : trimmed.StartsWith("CVSS:3.0/", StringComparison.OrdinalIgnoreCase) ? "3.0"
            : null;

        if (version is null)
        {
            return null;
        }

        var metrics = ParseMetrics(trimmed);
        if (metrics is null)
        {
            return null;
        }

        var scopeChanged = metrics["S"] == "C";

        if (!TryWeight(AttackVector, metrics, "AV", out var av)
            || !TryWeight(AttackComplexity, metrics, "AC", out var ac)
            || !TryWeight(scopeChanged ? PrivilegesRequiredScopeChanged : PrivilegesRequiredScopeUnchanged, metrics, "PR", out var pr)
            || !TryWeight(UserInteraction, metrics, "UI", out var ui)
            || !TryWeight(Impact, metrics, "C", out var confidentiality)
            || !TryWeight(Impact, metrics, "I", out var integrity)
            || !TryWeight(Impact, metrics, "A", out var availability))
        {
            return null;
        }

        // ISCBase — how much of the CIA triad is lost, combined so that damage to one dimension
        // cannot be undone by another being intact.
        var iscBase = 1 - ((1 - confidentiality) * (1 - integrity) * (1 - availability));

        var impactSubScore = scopeChanged
            ? (7.52 * (iscBase - 0.029)) - (3.25 * Math.Pow(iscBase - 0.02, 15))
            : 6.42 * iscBase;

        var exploitability = 8.22 * av * ac * pr * ui;

        // No impact means no base score, whatever the exploitability — an attack that achieves
        // nothing scores zero however easy it is.
        if (impactSubScore <= 0)
        {
            return new CvssScore(0.0, "NONE", version);
        }

        var raw = scopeChanged
            ? Math.Min(1.08 * (impactSubScore + exploitability), 10)
            : Math.Min(impactSubScore + exploitability, 10);

        var score = RoundUp(raw);
        return new CvssScore(score, Band(score), version);
    }

    /// <summary>
    /// CVSS v3.1's own rounding, which is not <c>Math.Ceiling(x * 10) / 10</c>.
    /// </summary>
    /// <remarks>
    /// The v3.1 specification defines rounding in integer arithmetic rather than as
    /// <c>Math.Ceiling(x * 10) / 10</c>, to keep a value that is mathematically an exact tenth
    /// from being pushed up a step by floating-point representation. This is that definition.
    ///
    /// Worth knowing before anyone "simplifies" it: across all 2592 possible v3.x base vectors
    /// the two forms agree, so no test here can distinguish them and none pretends to. It is
    /// written the specification's way because that is what the specification says, not because
    /// a base score is known to need it — and because the same rounding is what temporal and
    /// environmental scoring would use if this ever computed those, where the values being
    /// rounded are no longer a small fixed set.
    ///
    /// Applied to v3.0 vectors too: v3.0 says only "round up to one decimal place", which is
    /// exactly what this computes.
    /// </remarks>
    private static double RoundUp(double value)
    {
        var scaled = (int)Math.Round(value * 100000, MidpointRounding.AwayFromZero);
        return scaled % 10000 == 0
            ? scaled / 100000.0
            : (Math.Floor(scaled / 10000.0) + 1) / 10.0;
    }

    /// <summary>NVD's own bands, so a derived score sorts and colours the same way a published
    /// one does.</summary>
    private static string Band(double score) => score switch
    {
        <= 0.0 => "NONE",
        < 4.0 => "LOW",
        < 7.0 => "MEDIUM",
        < 9.0 => "HIGH",
        _ => "CRITICAL"
    };

    /// <summary>
    /// Splits <c>CVSS:3.1/AV:N/AC:L/...</c> into its metrics, or null if a base metric is missing.
    /// </summary>
    /// <remarks>
    /// Temporal and environmental metrics may legitimately follow the base ones and are ignored
    /// rather than rejected — this computes a *base* score, and a vector carrying more than the
    /// base is still a valid source for it.
    /// </remarks>
    private static Dictionary<string, string>? ParseMetrics(string vector)
    {
        var metrics = new Dictionary<string, string>(StringComparer.Ordinal);

        // Skips the leading "CVSS:3.x" component.
        foreach (var part in vector.Split('/').Skip(1))
        {
            var separator = part.IndexOf(':');
            if (separator <= 0 || separator == part.Length - 1)
            {
                return null;
            }

            metrics[part[..separator].ToUpperInvariant()] = part[(separator + 1)..].ToUpperInvariant();
        }

        foreach (var required in new[] { "AV", "AC", "PR", "UI", "S", "C", "I", "A" })
        {
            if (!metrics.ContainsKey(required))
            {
                return null;
            }
        }

        return metrics["S"] is "U" or "C" ? metrics : null;
    }

    private static bool TryWeight(
        IReadOnlyDictionary<string, double> weights, IReadOnlyDictionary<string, string> metrics, string metric, out double weight) =>
        weights.TryGetValue(metrics[metric], out weight);

    private static readonly Dictionary<string, double> AttackVector =
        new() { ["N"] = 0.85, ["A"] = 0.62, ["L"] = 0.55, ["P"] = 0.2 };

    private static readonly Dictionary<string, double> AttackComplexity =
        new() { ["L"] = 0.77, ["H"] = 0.44 };

    /// <summary>Privileges Required is weighted differently when the scope changes — escalating
    /// out of the vulnerable component makes holding privileges in it worth more to an attacker,
    /// so the penalty for needing them is smaller.</summary>
    private static readonly Dictionary<string, double> PrivilegesRequiredScopeUnchanged =
        new() { ["N"] = 0.85, ["L"] = 0.62, ["H"] = 0.27 };

    private static readonly Dictionary<string, double> PrivilegesRequiredScopeChanged =
        new() { ["N"] = 0.85, ["L"] = 0.68, ["H"] = 0.50 };

    private static readonly Dictionary<string, double> UserInteraction =
        new() { ["N"] = 0.85, ["R"] = 0.62 };

    private static readonly Dictionary<string, double> Impact =
        new() { ["H"] = 0.56, ["L"] = 0.22, ["N"] = 0.0 };
}

/// <param name="Version">Which CVSS revision the vector declared — recorded because scores are
/// not comparable across revisions.</param>
public record CvssScore(double BaseScore, string Severity, string Version)
{
    public string BaseScoreText => BaseScore.ToString("0.0", CultureInfo.InvariantCulture);
}
