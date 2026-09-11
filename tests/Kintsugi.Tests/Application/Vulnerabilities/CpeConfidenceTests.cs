using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Vulnerabilities;

/// <summary>
/// What the mapping queue's Confidence column is allowed to claim.
///
/// The column exists to tell the rows a reviewer can accept at a glance from the ones NVD's
/// dictionary is about to attribute somebody else's CVEs to, so the cases that matter most are the
/// near-misses: "slack" against slackware:slackware_linux is exactly the mistake the whole feature
/// is guarding against, and it must not read as anything but Low.
/// </summary>
public class CpeConfidenceTests
{
    private static CpeConfidenceAssessment Suggested(string displayName, string vendor, string product) =>
        CpeConfidence.Assess(displayName, vendor, product, CpeMappingStatus.Suggested, CpeSuggestionSource.Ai);

    [Fact]
    public void NothingProposed_ScoresNoneAndSaysNothing()
    {
        var assessment = CpeConfidence.Assess(
            "In-House Tool", null, null, CpeMappingStatus.Unmapped, CpeSuggestionSource.None);

        Assert.Equal(CpeConfidenceLevel.None, assessment.Level);
        Assert.Null(assessment.Reason);
    }

    [Fact]
    public void AHumanConfirming_OutranksAnyStringComparison()
    {
        // The reviewer had the dictionary, the installed versions and the product's own site in
        // front of them. A name that looks nothing like the CPE is exactly the case they were
        // needed for — Acrobat Reader is adobe:acrobat_reader_dc — so it must not be marked down.
        var assessment = CpeConfidence.Assess(
            "1Password 8", "agilebits", "1password", CpeMappingStatus.Confirmed, CpeSuggestionSource.Manual);

        Assert.Equal(CpeConfidenceLevel.High, assessment.Level);
        Assert.Contains("Confirmed by a person", assessment.Reason);
    }

    [Fact]
    public void ConfirmedInBulk_StillNamesWhereThePairCameFrom()
    {
        // The bulk confirm carries the suggestion's source through rather than stamping it Manual,
        // so the tooltip can say a person accepted a machine's proposal without typing it.
        var assessment = CpeConfidence.Assess(
            "Firefox", "mozilla", "firefox", CpeMappingStatus.Confirmed, CpeSuggestionSource.Ai);

        Assert.Equal(CpeConfidenceLevel.High, assessment.Level);
        Assert.Contains("AI", assessment.Reason);
    }

    [Theory]
    [InlineData("Google Chrome", "google", "chrome")]
    [InlineData("Firefox", "mozilla", "firefox")]
    [InlineData("Visual Studio Code", "microsoft", "visual_studio_code")]
    [InlineData("1Password", "agilebits", "1password")]
    [InlineData("Mozilla Firefox", "mozilla", "firefox")]
    public void AnExactNameMatch_ScoresHigh(string displayName, string vendor, string product)
    {
        // Spacing, casing and CPE's underscores are not differences worth reading.
        Assert.Equal(CpeConfidenceLevel.High, Suggested(displayName, vendor, product).Level);
    }

    [Theory]
    [InlineData("Zoom", "zoom", "zoom_workplace_desktop")]
    [InlineData("Acrobat Pro", "adobe", "acrobat_dc")]
    [InlineData("Acrobat Reader", "adobe", "acrobat_reader_dc")]
    public void APartialMatch_ScoresMediumAndSaysWhatToCheck(string displayName, string vendor, string product)
    {
        var assessment = Suggested(displayName, vendor, product);

        Assert.Equal(CpeConfidenceLevel.Medium, assessment.Level);
        Assert.NotNull(assessment.Reason);
    }

    [Theory]
    [InlineData("Slack", "slackware", "slackware_linux")]
    [InlineData("Zoom", "aisquared", "zoomtext")]
    [InlineData("Our Deploy Tool", "hashicorp", "terraform")]
    public void TheMistakeThisFeatureExistsToCatch_ScoresLow(string displayName, string vendor, string product)
    {
        var assessment = Suggested(displayName, vendor, product);

        Assert.Equal(CpeConfidenceLevel.Low, assessment.Level);
        Assert.Contains("wrong mapping", assessment.Reason);
    }

    [Fact]
    public void ShortNames_DoNotMatchOnTwoLetters()
    {
        // "Go" appearing inside "mongodb" is not evidence of anything, and a three-character floor
        // is what keeps the Low band honest.
        Assert.Equal(CpeConfidenceLevel.Low, Suggested("Go", "mongodb", "mongodb").Level);
    }

    [Fact]
    public void ASubstringInsideALongerWord_IsNotAPartialMatch()
    {
        // The whole point of the word floor. "Slack" sits inside "slackware_linux" and "Zoom"
        // inside "zoomtext", so anything that tests for containment rather than for whole words
        // presents both of this screen's canonical wrong answers as near misses worth accepting.
        Assert.Equal(CpeConfidenceLevel.Low, Suggested("Slack", "slackware", "slackware_linux").Level);
        Assert.Equal(CpeConfidenceLevel.High, Suggested("Slack", "slack", "slack").Level);
    }

    [Fact]
    public void EveryScoredRow_CarriesTheVendorAndProductInItsReason()
    {
        // The tooltip is read beside a table of dozens of rows; "Medium" alone tells a reviewer
        // nothing about which pair they are being asked to weigh.
        var assessment = Suggested("Zoom", "zoom", "zoom_workplace_desktop");

        Assert.Contains("zoom_workplace_desktop", assessment.Reason);
    }
}
