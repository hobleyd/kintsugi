using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;
using Kintsugi.Application.Hosts;
using Kintsugi.Application.Hosts.Commands.CreateHost;
using Kintsugi.Application.Hosts.Commands.ReportOperatingSystemPatched;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application;

public class HostRestartStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARestartReportedForTheFirstTime_StartsTheClockAndNamesTheCause()
    {
        var host = new Host("h", "S1", "Ubuntu 24.04");

        host.RecordRestartState(true, "linux-image-6.8.0-52-generic", T0);

        Assert.Equal(T0, host.RestartRequiredSinceUtc);
        Assert.Equal("linux-image-6.8.0-52-generic", host.RestartRequiredBecause);
    }

    /// <summary>"Since" is when it was first needed, not the last check-in that repeated it.</summary>
    [Fact]
    public void TheSameRestartReportedAgain_KeepsTheOriginalTime()
    {
        var host = new Host("h", "S1");
        host.RecordRestartState(true, "a", T0);

        host.RecordRestartState(true, "a, b", T0.AddHours(5));

        Assert.Equal(T0, host.RestartRequiredSinceUtc);
        Assert.Equal("a, b", host.RestartRequiredBecause);
    }

    /// <summary>An explicit "no" is how the server learns somebody restarted the host.</summary>
    [Fact]
    public void ReportingNoRestartNeeded_ClearsBoth()
    {
        var host = new Host("h", "S1");
        host.RecordRestartState(true, "a", T0);

        host.RecordRestartState(false, null, T0.AddHours(1));

        Assert.Null(host.RestartRequiredSinceUtc);
        Assert.Null(host.RestartRequiredBecause);
    }

    /// <summary>
    /// Every agent but the Linux one stays silent, and so does a Linux agent that predates the field.
    /// Silence must not clear a restart the host has reported.
    /// </summary>
    [Fact]
    public void NotReported_LeavesWhatIsStored()
    {
        var host = new Host("h", "S1");
        host.RecordRestartState(true, "a", T0);

        host.RecordRestartState(null, null, T0.AddHours(1));

        Assert.Equal(T0, host.RestartRequiredSinceUtc);
        Assert.Equal("a", host.RestartRequiredBecause);
    }

    [Fact]
    public void ALongCause_IsCutToTheColumnWidth()
    {
        var host = new Host("h", "S1");

        host.RecordRestartState(true, new string('x', 900), T0);

        Assert.Equal(Host.MaxRestartBecauseLength, host.RestartRequiredBecause!.Length);
    }

    [Fact]
    public void ACheckIn_RecordsTheRestartState()
    {
        var host = new Host("h", "S1");

        host.Reregister("h", "Ubuntu", null, restartRequired: true, restartBecause: "linux-base");

        Assert.NotNull(host.RestartRequiredSinceUtc);
        Assert.Equal("linux-base", host.RestartRequiredBecause);
    }

    [Fact]
    public void ACheckInThatSaysNothing_DoesNotClearIt()
    {
        var host = new Host("h", "S1");
        host.Reregister("h", "Ubuntu", null, restartRequired: true, restartBecause: "linux-base");

        host.Reregister("h", "Ubuntu", null);

        Assert.NotNull(host.RestartRequiredSinceUtc);
    }

    [Fact]
    public void TheHostDto_CarriesIt()
    {
        var host = new Host("h", "S1");
        host.RecordRestartState(true, "linux-base", T0);

        var dto = HostDto.FromEntity(host);

        Assert.Equal(T0, dto.RestartRequiredSinceUtc);
        Assert.Equal("linux-base", dto.RestartRequiredBecause);
    }

    [Fact]
    public async Task ReportingAnOperatingSystemPatch_RecordsTheRestartTheAgentFound()
    {
        var host = new Host("h", "S1", "Ubuntu 24.04", operatingSystemUpdateAvailable: true);
        var hosts = new Mock<IHostRepository>();
        hosts.Setup(r => r.GetBySerialNumberAsync("S1", It.IsAny<CancellationToken>())).ReturnsAsync(host);
        var failures = new Mock<IPatchFailureRepository>();
        failures.Setup(r => r.GetOutstandingForApplicationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var handler = new ReportOperatingSystemPatchedCommandHandler(hosts.Object, failures.Object, new Mock<IUnitOfWork>().Object, new Mock<IHostEventRecorder>().Object);

        await handler.Handle(new ReportOperatingSystemPatchedCommand("S1", true, "linux-image-6.8.0-52-generic"), CancellationToken.None);

        Assert.Equal("linux-image-6.8.0-52-generic", host.RestartRequiredBecause);
    }

    [Fact]
    public void ARestartCauseLongerThanTheColumn_DoesNotFailTheCheckIn()
    {
        // Refusing it refused the whole check-in, and the host went stale; the domain cuts it instead.
        var tooLong = new string('x', Host.MaxRestartBecauseLength + 1);

        Assert.True(new CreateHostCommandValidator().Validate(new CreateHostCommand("h", "S1", RestartBecause: tooLong)).IsValid);
        Assert.True(new ReportOperatingSystemPatchedCommandValidator().Validate(new ReportOperatingSystemPatchedCommand("S1", true, tooLong)).IsValid);
    }

    [Fact]
    public void ALongCause_IsNeverCutInsideASurrogatePair()
    {
        var host = new Host("h", "S1");
        var cause = new string('x', Host.MaxRestartBecauseLength - 1) + "\U0001F600" + "tail";

        host.RecordRestartState(true, cause, T0);

        Assert.Equal(Host.MaxRestartBecauseLength - 1, host.RestartRequiredBecause!.Length);
        Assert.False(char.IsHighSurrogate(host.RestartRequiredBecause[^1]));
    }
}
