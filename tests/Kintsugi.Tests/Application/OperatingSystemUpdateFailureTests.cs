using Kintsugi.Application.PatchFailures;

namespace Kintsugi.Tests.Application;

public class OperatingSystemUpdateFailureTests
{
    [Theory]
    [InlineData("Windows", "Windows Update")]
    [InlineData("macOS", "macOS update")]
    [InlineData("Linux", "Linux system update")]
    public void TheThreeAgentNamesAreOperatingSystemUpdates(string name, string display)
    {
        Assert.True(OperatingSystemUpdateFailure.IsOperatingSystem(name));
        Assert.Equal(display, OperatingSystemUpdateFailure.DisplayName(name));
    }

    [Theory]
    [InlineData("Slack")]
    [InlineData("windows")]
    [InlineData("Windows Terminal")]
    public void AnApplicationIsNotAnOperatingSystemUpdate(string name)
    {
        Assert.False(OperatingSystemUpdateFailure.IsOperatingSystem(name));
        Assert.Null(OperatingSystemUpdateFailure.Explain(name, "exit code 1"));
    }

    [Theory]
    [InlineData("Windows", "the Windows Update install exited with exit code: 1: 0x80070070 There is not enough space on the disk", "Not enough disk space")]
    [InlineData("Windows", "exited with exit code: 1: 0x80070643 Fatal error during installation", "The installer reported a fatal error")]
    [InlineData("Windows", "Windows Update exit code: 3010", "A restart is needed first")]
    [InlineData("Windows", "Windows failed to install the following update with error 0x80073D02: 9N9PHDT62W94-AD2F1837.myHP", "An app was open and blocked the update")]
    [InlineData("Windows", "error 0x80072EE2", "Windows Update could not be reached")]
    [InlineData("macOS", "softwareupdate: not authorized to install", "macOS would not authorise the install")]
    [InlineData("Linux", "E: Could not get lock /var/lib/dpkg/lock-frontend - open (11: Resource temporarily unavailable)", "Another package manager is running")]
    [InlineData("Linux", "E: dpkg was interrupted, you must manually run 'dpkg --configure -a'", "An earlier install was interrupted")]
    [InlineData("Linux", "W: GPG error: NO_PUBKEY 4EB27DB2A3B88B8B", "A repository's signing key is missing or expired")]
    public void KnownMessagesGetAPlainCause(string name, string details, string title)
    {
        var cause = OperatingSystemUpdateFailure.Explain(name, details);

        Assert.NotNull(cause);
        Assert.Equal(title, cause!.Title);
        Assert.False(string.IsNullOrWhiteSpace(cause.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(cause.SuggestedFix));
    }

    [Fact]
    public void AWindowsMessageDoesNotMatchALinuxRule()
    {
        var cause = OperatingSystemUpdateFailure.Explain("Windows", "E: Could not get lock /var/lib/dpkg/lock-frontend");

        Assert.Equal("The update ran and failed", cause!.Title);
    }

    [Fact]
    public void AnUnrecognisedMessageIsSaidHonestlyRatherThanGuessed()
    {
        var cause = OperatingSystemUpdateFailure.Explain("Linux", "something nobody has seen before");

        Assert.Equal("The update ran and failed", cause!.Title);
        Assert.Contains("does not recognise", cause.Explanation);
    }

    [Fact]
    public void TheDtoCarriesTheCauseSoTheScreenNeedsNoRulesOfItsOwn()
    {
        var dto = new PatchFailureDto(
            Guid.NewGuid(), Guid.NewGuid(), "host", "SN", "Windows", null, "Windows 11", null,
            "exit code: 1: 0x80070070", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 2,
            Kintsugi.Domain.Enums.PatchFailureResolution.Outstanding, null, false, false, default);

        Assert.True(dto.IsOperatingSystemUpdate);
        Assert.Equal("Windows Update", dto.DisplayName);
        Assert.Equal("Not enough disk space", dto.CauseTitle);
        Assert.NotNull(dto.SuggestedFix);
    }
}
