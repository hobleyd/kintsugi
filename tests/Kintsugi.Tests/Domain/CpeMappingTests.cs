using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class CpeMappingTests
{
    private static CpeMapping AnApplication(string name = "Firefox") =>
        CpeMapping.Discover(CpeSubjectKind.Application, name, name);

    [Fact]
    public void Discover_NormalizesTheSubjectKey()
    {
        // "Google Chrome" and "Google chrome" have to be one mapping, or a human confirms the
        // same product twice and the fleet's coverage silently depends on casing.
        var upper = CpeMapping.Discover(CpeSubjectKind.Application, "Google Chrome", "Google Chrome");
        var lower = CpeMapping.Discover(CpeSubjectKind.Application, "google chrome", "google chrome");

        Assert.Equal(upper.SubjectKey, lower.SubjectKey);
    }

    [Fact]
    public void Discover_StartsUnmappedAndUnassessable()
    {
        var mapping = AnApplication();

        Assert.Equal(CpeMappingStatus.Unmapped, mapping.Status);
        Assert.False(mapping.IsAssessable);
    }

    [Fact]
    public void Part_FollowsTheSubjectKind()
    {
        Assert.Equal("a", AnApplication().Part);
        Assert.Equal("o", CpeMapping.Discover(CpeSubjectKind.OperatingSystem, "macos", "macOS").Part);
    }

    [Fact]
    public void ToCpeName_ProducesTheFormNvdMatchesOn()
    {
        var mapping = AnApplication();
        mapping.Confirm("mozilla", "firefox", CpeSuggestionSource.Manual);

        Assert.Equal("cpe:2.3:a:mozilla:firefox:130.0:*:*:*:*:*:*:*", mapping.ToCpeName("130.0"));
    }

    [Fact]
    public void ToCpeName_EscapesCharactersThatWouldShiftTheComponents()
    {
        // An unescaped colon in a version would move every later component one place along,
        // turning a query about a version into a query about an edition.
        var mapping = AnApplication();
        mapping.Confirm("apache", "tomcat", CpeSuggestionSource.Manual);

        Assert.Equal(@"cpe:2.3:a:apache:tomcat:9.0\:beta:*:*:*:*:*:*:*", mapping.ToCpeName("9.0:beta"));
    }

    [Fact]
    public void ToCpeName_LeavesAnOrdinaryVersionUntouched()
    {
        var mapping = AnApplication();
        mapping.Confirm("microsoft", "windows_11_23h2", CpeSuggestionSource.Manual);

        Assert.Equal("cpe:2.3:a:microsoft:windows_11_23h2:10.0.22631.4317:*:*:*:*:*:*:*", mapping.ToCpeName("10.0.22631.4317"));
    }

    [Fact]
    public void ToCpeName_BeforeAnythingIsConfirmed_Throws()
    {
        Assert.Throws<DomainException>(() => AnApplication().ToCpeName("1.0"));
    }

    [Fact]
    public void Suggest_DoesNotMakeASubjectAssessable()
    {
        // The review step is the whole safety property: a machine's proposal must never produce
        // findings on its own.
        var mapping = AnApplication();
        mapping.Suggest("mozilla", "firefox", CpeSuggestionSource.Ai, "high confidence");

        Assert.Equal(CpeMappingStatus.Suggested, mapping.Status);
        Assert.False(mapping.IsAssessable);
    }

    [Fact]
    public void Suggest_OverAConfirmedMapping_Throws()
    {
        // A confirmation is a human decision. Letting a background run overwrite it would
        // silently re-point findings at a different product between one run and the next.
        var mapping = AnApplication();
        mapping.Confirm("mozilla", "firefox", CpeSuggestionSource.Manual);

        Assert.Throws<DomainException>(() => mapping.Suggest("apple", "safari", CpeSuggestionSource.Ai, null));
    }

    [Fact]
    public void Confirm_MakesASubjectAssessable()
    {
        var mapping = AnApplication();
        mapping.Confirm("mozilla", "firefox", CpeSuggestionSource.Manual);

        Assert.Equal(CpeMappingStatus.Confirmed, mapping.Status);
        Assert.True(mapping.IsAssessable);
        Assert.NotNull(mapping.ConfirmedAtUtc);
    }

    [Fact]
    public void Confirm_LowercasesTheComponents()
    {
        var mapping = AnApplication();
        mapping.Confirm("Mozilla", "Firefox", CpeSuggestionSource.Manual);

        Assert.Equal("mozilla", mapping.Vendor);
        Assert.Equal("firefox", mapping.Product);
    }

    [Theory]
    [InlineData("moz:illa")]
    [InlineData("moz*illa")]
    [InlineData("moz illa")]
    public void Confirm_RefusesComponentsThatWouldChangeTheShapeOfEveryCpeName(string vendor)
    {
        Assert.Throws<DomainException>(() => AnApplication().Confirm(vendor, "firefox", CpeSuggestionSource.Manual));
    }

    [Fact]
    public void Confirm_OnAFreshMapping_IsNotARepoint()
    {
        var mapping = AnApplication();
        mapping.Confirm("mozilla", "firefox", CpeSuggestionSource.Manual);

        Assert.False(mapping.Repointed);
    }

    [Fact]
    public void Confirm_MovingAnAlreadyConfirmedMappingToAnotherProduct_IsARepoint()
    {
        // What tells the handler to discard the stored assessments: they describe a product this
        // subject no longer claims to be.
        var mapping = AnApplication();
        mapping.Confirm("mozilla", "firefox", CpeSuggestionSource.Manual);
        mapping.Confirm("apple", "safari", CpeSuggestionSource.Manual);

        Assert.True(mapping.Repointed);
    }

    [Fact]
    public void Confirm_ReconfirmingTheSameProduct_IsNotARepoint()
    {
        var mapping = AnApplication();
        mapping.Confirm("mozilla", "firefox", CpeSuggestionSource.Manual);
        mapping.Confirm("mozilla", "firefox", CpeSuggestionSource.Manual);

        Assert.False(mapping.Repointed);
    }

    [Fact]
    public void MarkNotApplicable_ClearsTheProductAndStopsAssessment()
    {
        var mapping = AnApplication("Our Internal Tool");
        mapping.Confirm("mozilla", "firefox", CpeSuggestionSource.Manual);
        mapping.MarkNotApplicable("In-house, NVD does not track it.");

        Assert.Equal(CpeMappingStatus.NotApplicable, mapping.Status);
        Assert.Null(mapping.Vendor);
        Assert.False(mapping.IsAssessable);
    }

    [Fact]
    public void Reset_ReturnsASubjectToTheQueue()
    {
        var mapping = AnApplication();
        mapping.Suggest("mozilla", "firefox", CpeSuggestionSource.Ai, "notes");
        mapping.Reset();

        Assert.Equal(CpeMappingStatus.Unmapped, mapping.Status);
        Assert.Equal(CpeSuggestionSource.None, mapping.SuggestionSource);
        Assert.Null(mapping.SuggestionNotes);
    }

    [Fact]
    public void ToCpeMatchString_AsksAboutAProductRatherThanAVersion()
    {
        // The form the dictionary check uses. Verified against the live API: this shape answers
        // 1199 for mozilla:firefox and 0 for the invented mozilla:firefax.
        Assert.Equal(
            "cpe:2.3:a:mozilla:firefox:*:*:*:*:*:*:*:*",
            CpeMapping.ToCpeMatchString("a", "mozilla", "firefox"));
    }

    [Fact]
    public void Touch_MovesTheRowDownTheSuggestionQueue()
    {
        // Without it a name the model cannot map sits at the head of the queue forever and is
        // re-asked every run, starving everything behind it.
        var mapping = AnApplication();
        Assert.Null(mapping.UpdatedAtUtc);

        mapping.Touch();

        Assert.NotNull(mapping.UpdatedAtUtc);
    }
}
