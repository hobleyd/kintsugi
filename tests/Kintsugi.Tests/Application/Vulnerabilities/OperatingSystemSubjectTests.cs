using Kintsugi.Application.Vulnerabilities;

namespace Kintsugi.Tests.Application.Vulnerabilities;

public class OperatingSystemSubjectTests
{
    [Theory]
    [InlineData("macOS 14.5", "14.5")]
    [InlineData("macOS 26.1", "26.1")]
    [InlineData("macOS 15.7.2", "15.7.2")]
    public void Derive_MacOs_ReadsTheProductVersion(string reported, string expected)
    {
        var subject = OperatingSystemSubject.Derive(reported, null, null);

        Assert.NotNull(subject);
        Assert.Equal("macos", subject.SubjectKey);
        Assert.Equal(expected, subject.Version);
        Assert.True(subject.IsAssessable);
    }

    [Fact]
    public void Derive_MacOs_NeedsNoAgentChange()
    {
        // sw_vers -productVersion already gives exactly what NVD's ranges are evaluated against,
        // which is why the macOS agent is the one of the three that needed nothing added.
        var subject = OperatingSystemSubject.Derive("macOS 14.5", null, null);

        Assert.Null(subject!.UnassessableReason);
    }

    [Fact]
    public void Derive_Windows_WithoutTheUpdateRevision_RefusesToAssess()
    {
        // The whole point of the Windows agent change. NVD evaluates Windows CVE ranges against
        // the revision — build 22631.4317 answers 1355 CVEs and 22631.6000 answers 793 — so
        // assuming ".0" would report nearly every Windows CVE ever filed against a fully patched
        // machine. A wrong answer here is the one an administrator would act on.
        var subject = OperatingSystemSubject.Derive("Windows 11 Pro 23H2 (22631)", null, null);

        Assert.NotNull(subject);
        Assert.Equal("windows_11_23h2", subject.SubjectKey);
        Assert.Null(subject.Version);
        Assert.False(subject.IsAssessable);
        Assert.Contains("22631", subject.UnassessableReason);
        Assert.Contains("Upgrade the agent", subject.UnassessableReason);
    }

    [Fact]
    public void Derive_Windows_WithTheUpdateRevision_BuildsTheFullVersion()
    {
        var subject = OperatingSystemSubject.Derive("Windows 11 Pro 23H2 (22631)", null, "22631.4317");

        Assert.Equal("windows_11_23h2", subject!.SubjectKey);
        Assert.Equal("10.0.22631.4317", subject.Version);
        Assert.Null(subject.UnassessableReason);
    }

    [Fact]
    public void Derive_Windows_DoesNotDoubleThePrefixWhenTheAgentAlreadySentOne()
    {
        var subject = OperatingSystemSubject.Derive("Windows 11 Pro 23H2 (22631)", null, "10.0.22631.4317");

        Assert.Equal("10.0.22631.4317", subject!.Version);
    }

    [Fact]
    public void Derive_Windows_DropsTheEdition()
    {
        // NVD indexes Windows by family and feature update, not by SKU. Keeping the edition would
        // split one mapping into as many rows as the fleet has editions and ask a human to
        // confirm each identically.
        var pro = OperatingSystemSubject.Derive("Windows 11 Pro 23H2 (22631)", null, "22631.4317");
        var enterprise = OperatingSystemSubject.Derive("Windows 11 Enterprise 23H2 (22631)", null, "22631.4317");

        Assert.Equal(pro!.SubjectKey, enterprise!.SubjectKey);
    }

    [Fact]
    public void Derive_Windows_TellsFeatureUpdatesApart()
    {
        var older = OperatingSystemSubject.Derive("Windows 11 Pro 22H2 (22621)", null, "22621.1000");
        var newer = OperatingSystemSubject.Derive("Windows 11 Pro 23H2 (22631)", null, "22631.4317");

        Assert.NotEqual(older!.SubjectKey, newer!.SubjectKey);
    }

    [Fact]
    public void Derive_Linux_WithOsReleaseFacts_UsesThemRatherThanThePrettyName()
    {
        // PRETTY_NAME is prose — "Ubuntu 24.04.1 LTS" carries a point release NVD does not index
        // by, and "openSUSE Leap 15.6" and "Alpine v3.20" share no grammar with either.
        var subject = OperatingSystemSubject.Derive("Ubuntu 24.04.1 LTS (Linux)", "ubuntu", "24.04");

        Assert.Equal("ubuntu", subject!.SubjectKey);
        Assert.Equal("24.04", subject.Version);
        Assert.True(subject.IsAssessable);
    }

    [Fact]
    public void Derive_Linux_WithoutOsReleaseFacts_RefusesToAssess()
    {
        var subject = OperatingSystemSubject.Derive("Ubuntu 24.04.1 LTS (Linux)", null, null);

        Assert.False(subject!.IsAssessable);
        Assert.Contains("does not report", subject.UnassessableReason);
    }

    [Fact]
    public void Derive_Linux_WithAnIdButNoVersion_SaysSo()
    {
        var subject = OperatingSystemSubject.Derive("Arch Linux", "arch", null);

        Assert.Equal("arch", subject!.SubjectKey);
        Assert.False(subject.IsAssessable);
        Assert.Contains("VERSION_ID", subject.UnassessableReason);
    }

    [Fact]
    public void Derive_AnOsReleaseIdBeatsTheReportedName()
    {
        // The agent has told us exactly what this is; nothing inferred from prose should override
        // that. Guards the ordering inside Derive, which is easy to reverse by accident.
        var subject = OperatingSystemSubject.Derive("Some Linux That Mentions Windows", "debian", "12");

        Assert.Equal("debian", subject!.SubjectKey);
        Assert.Equal("12", subject.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Derive_WithNothingReported_ReturnsNull(string? reported)
    {
        // A host that enrolled and has never checked in. Distinct from an unassessable one, which
        // is counted on the screen.
        Assert.Null(OperatingSystemSubject.Derive(reported, null, null));
    }

    [Fact]
    public void Derive_AnUnknownPlatform_IsQueuedRatherThanDropped()
    {
        // Coverage this feature does not have has to be visible. A subject dropped here would
        // leave the fleet looking fully assessed.
        var subject = OperatingSystemSubject.Derive("FreeBSD 14.1-RELEASE", null, null);

        Assert.NotNull(subject);
        Assert.False(subject.IsAssessable);
        Assert.NotNull(subject.UnassessableReason);
    }
}
