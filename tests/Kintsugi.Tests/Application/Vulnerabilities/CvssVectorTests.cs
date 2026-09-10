using Kintsugi.Application.Vulnerabilities;

namespace Kintsugi.Tests.Application.Vulnerabilities;

public class CvssVectorTests
{
    /// <summary>
    /// Every expected value here is the score NVD actually publishes for that vector, not one
    /// this implementation produced and was then written down. That is the whole point: a base
    /// score is a pure function of its vector, so if these agree with NVD the derivation is not
    /// an approximation of NVD's answer, it is NVD's answer.
    /// </summary>
    [Theory]
    // CVE-2024-9680, the exploited Firefox use-after-free this fleet's own test data matched.
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H", 9.8, "CRITICAL")]
    // CVE-2023-4911, "Looney Tunables" — the glibc escalation the Linux package path surfaced.
    [InlineData("CVSS:3.1/AV:L/AC:L/PR:L/UI:N/S:U/C:H/I:H/A:H", 7.8, "HIGH")]
    // CVE-2024-2511, an OpenSSL memory-growth DoS.
    [InlineData("CVSS:3.1/AV:N/AC:H/PR:N/UI:N/S:U/C:N/I:N/A:H", 5.9, "MEDIUM")]
    // The classic reflected-XSS shape, and the one that exercises the scope-changed branch.
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:R/S:C/C:L/I:L/A:N", 6.1, "MEDIUM")]
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:L/UI:N/S:U/C:N/I:N/A:H", 6.5, "MEDIUM")]
    // Band boundaries, where an off-by-one in the rounding would show up as the wrong colour.
    [InlineData("CVSS:3.1/AV:L/AC:H/PR:N/UI:R/S:U/C:H/I:H/A:H", 7.0, "HIGH")]
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:C/C:H/I:H/A:H", 10.0, "CRITICAL")]
    public void Score_MatchesTheScoreNvdPublishesForTheSameVector(string vector, double expected, string severity)
    {
        var score = CvssVector.Score(vector);

        Assert.NotNull(score);
        Assert.Equal(expected, score.BaseScore);
        Assert.Equal(severity, score.Severity);
        Assert.Equal("3.1", score.Version);
    }

    [Fact]
    public void Score_WithNoImpactAtAll_IsZeroRatherThanUnscored()
    {
        // An attack that achieves nothing scores zero however easy it is — and zero is a real
        // answer, distinct from "nobody has scored this", which is null.
        var score = CvssVector.Score("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:N");

        Assert.NotNull(score);
        Assert.Equal(0.0, score.BaseScore);
        Assert.Equal("NONE", score.Severity);
    }

    [Fact]
    public void Score_ReadsAV30Vector_AndSaysSo()
    {
        // Same base formula, and the version is recorded because scores are not comparable
        // across revisions.
        var score = CvssVector.Score("CVSS:3.0/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H");

        Assert.Equal(9.8, score!.BaseScore);
        Assert.Equal("3.0", score.Version);
    }

    [Fact]
    public void Score_IgnoresTemporalAndEnvironmentalMetricsRatherThanRefusingTheVector()
    {
        // A vector carrying more than the base metrics is still a valid source for a *base*
        // score, which is what this computes.
        var withExtras = CvssVector.Score("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H/E:P/RL:O/RC:C");
        var baseOnly = CvssVector.Score("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H");

        Assert.Equal(baseOnly!.BaseScore, withExtras!.BaseScore);
    }

    [Theory]
    // v4.0 scores through a MacroVector lookup table, which is a different piece of work.
    [InlineData("CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N")]
    // v2 is a different formula again, and carries no "CVSS:" prefix.
    [InlineData("AV:N/AC:L/Au:N/C:P/I:P/A:P")]
    public void Score_RefusesARevisionItCannotCompute(string vector)
    {
        // Null, so the screen shows "unscored" rather than a number built from the wrong formula.
        Assert.Null(CvssVector.Score(vector));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Missing a base metric — Availability. A default here would produce a plausible number that
    // is not this vulnerability's score, which is worse than no number.
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H")]
    // An unrecognized metric value.
    [InlineData("CVSS:3.1/AV:X/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H")]
    // A malformed scope, which decides which of two formulas is used.
    [InlineData("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:Z/C:H/I:H/A:H")]
    [InlineData("CVSS:3.1/AV/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H")]
    public void Score_RefusesAVectorItCannotParse(string? vector)
    {
        Assert.Null(CvssVector.Score(vector));
    }

    [Fact]
    public void Score_RoundsUp_NeverDown()
    {
        // 8.3, not 8.2: the sum lands between the two and CVSS always rounds a base score up.
        var score = CvssVector.Score("CVSS:3.1/AV:N/AC:L/PR:L/UI:N/S:U/C:H/I:H/A:L");

        Assert.Equal(8.3, score!.BaseScore);
    }

    [Fact]
    public void Score_EveryPossibleBaseVector_IsWellFormed()
    {
        // All 2592 of them. Cheap, and it is the check that would catch a weight table typo or a
        // band boundary that a handful of known-value cases walks straight past: every vector
        // must score, land in range, sit on an exact tenth, and carry the band its own number
        // implies.
        var values = new[]
        {
            new[] { "N", "A", "L", "P" }, new[] { "L", "H" }, new[] { "N", "L", "H" },
            new[] { "N", "R" }, new[] { "U", "C" },
            new[] { "H", "L", "N" }, new[] { "H", "L", "N" }, new[] { "H", "L", "N" },
        };

        var checked_ = 0;

        foreach (var av in values[0])
        foreach (var ac in values[1])
        foreach (var pr in values[2])
        foreach (var ui in values[3])
        foreach (var s in values[4])
        foreach (var c in values[5])
        foreach (var i in values[6])
        foreach (var a in values[7])
        {
            var vector = $"CVSS:3.1/AV:{av}/AC:{ac}/PR:{pr}/UI:{ui}/S:{s}/C:{c}/I:{i}/A:{a}";
            var score = CvssVector.Score(vector);

            Assert.NotNull(score);
            Assert.InRange(score.BaseScore, 0.0, 10.0);
            Assert.Equal(Math.Round(score.BaseScore, 1), score.BaseScore);

            var expectedBand = score.BaseScore switch
            {
                <= 0.0 => "NONE",
                < 4.0 => "LOW",
                < 7.0 => "MEDIUM",
                < 9.0 => "HIGH",
                _ => "CRITICAL"
            };
            Assert.Equal(expectedBand, score.Severity);
            checked_++;
        }

        Assert.Equal(2592, checked_);
    }
}
