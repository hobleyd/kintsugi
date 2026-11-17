using Moq;
using Kintsugi.Application.Autonomy;
using Kintsugi.Application.Autonomy.Queries.GetAutonomy;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.DeviceHealth;
using Kintsugi.Application.DeviceHealth.Commands.ReportHostHealthEvents;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application;

public class HostHealthAssessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _host = Guid.NewGuid();

    private HealthEventRecord Shutdown(double daysAgo) => new(_host, HostEventKind.UnexpectedShutdown, Now.AddDays(-daysAgo));
    private HealthEventRecord Fatal(double daysAgo) => new(_host, HostEventKind.HardwareErrorFatal, Now.AddDays(-daysAgo));
    private HealthEventRecord Corrected(double daysAgo) => new(_host, HostEventKind.HardwareErrorCorrected, Now.AddDays(-daysAgo));

    private HostHealth Of(params HealthEventRecord[] events) => HostHealthAssessor.Assess(_host, events, Now).Health;

    [Fact]
    public void NothingLogged_IsHealthy() => Assert.Equal(HostHealth.Healthy, Of());

    [Fact]
    public void OneOrTwoUnexpectedShutdownsInAMonth_AreWorthWatching()
    {
        Assert.Equal(HostHealth.Watch, Of(Shutdown(20)));
        Assert.Equal(HostHealth.Watch, Of(Shutdown(20), Shutdown(3)));
    }

    /// <summary>500 reports are not hidden: a device sending that many is flagged, and the page says why.</summary>
    [Fact]
    public void ADeviceSendingAnUnusualNumberOfEvents_IsUnstableAndSaysSo()
    {
        var flood = Enumerable.Range(0, HostHealthAssessor.UnusualVolumeIn30Days).Select(i => Corrected(i * 0.2)).ToArray();

        var assessment = HostHealthAssessor.Assess(_host, flood, Now);

        Assert.Equal(HostHealth.Unstable, assessment.Health);
        Assert.Contains("far more than a device normally logs", assessment.Why);
        Assert.Equal(HostHealth.Watch, Of(flood.Take(HostHealthAssessor.UnusualVolumeIn30Days - 1).ToArray()));
    }

    [Fact]
    public void ThreeUnexpectedShutdownsInAWeek_AreUnstable() =>
        Assert.Equal(HostHealth.Unstable, Of(Shutdown(1), Shutdown(3), Shutdown(6)));

    /// <summary>Three over a month is not three in a week: the rule is a run, per device.</summary>
    [Fact]
    public void ThreeShutdownsSpreadOverAMonth_AreOnlyWorthWatching() =>
        Assert.Equal(HostHealth.Watch, Of(Shutdown(2), Shutdown(15), Shutdown(28)));

    [Fact]
    public void AnyFatalHardwareErrorInAMonth_IsUnstable() => Assert.Equal(HostHealth.Unstable, Of(Fatal(25)));

    [Fact]
    public void ACorrectedHardwareError_IsWorthWatching() => Assert.Equal(HostHealth.Watch, Of(Corrected(10)));

    [Fact]
    public void EventsOlderThanAMonth_DoNotCount() => Assert.Equal(HostHealth.Healthy, Of(Shutdown(31), Fatal(40)));

    [Fact]
    public void AnotherDevicesEvents_DoNotCount()
    {
        var other = new HealthEventRecord(Guid.NewGuid(), HostEventKind.HardwareErrorFatal, Now.AddDays(-1));

        Assert.Equal(HostHealth.Healthy, HostHealthAssessor.Assess(_host, [other], Now).Health);
    }

    [Fact]
    public void TheReasonIsInWordsAndNamesTheCounts()
    {
        var a = HostHealthAssessor.Assess(_host, [Shutdown(1), Shutdown(2), Shutdown(3), Shutdown(20), Fatal(5), Corrected(4)], Now);

        Assert.Equal(
            "3 unexpected shutdowns in the last 7 days, 4 in 30 days. 1 fatal hardware error in 30 days. 1 corrected hardware error in 30 days",
            a.Why);
    }

    [Fact]
    public void AssessAll_ListsOnlyUnhealthyDevicesWorstFirst()
    {
        var watch = Guid.NewGuid();
        var unstable = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        var events = new[]
        {
            new HealthEventRecord(watch, HostEventKind.UnexpectedShutdown, Now.AddDays(-5)),
            new HealthEventRecord(unstable, HostEventKind.HardwareErrorFatal, Now.AddDays(-2)),
            new HealthEventRecord(healthy, HostEventKind.UnexpectedShutdown, Now.AddDays(-45)),
        };

        var result = HostHealthAssessor.AssessAll(events, Now);

        Assert.Equal([unstable, watch], result.Select(a => a.HostId));
        Assert.Equal([HostHealth.Unstable, HostHealth.Watch], result.Select(a => a.Health));
    }
}

public class ReportHostHealthEventsCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hosts = new();
    private readonly Mock<IHostEventRepository> _events = new();
    private readonly Mock<IUnitOfWork> _unit = new();
    private readonly List<HostEvent> _added = [];
    private readonly Host _host = new("pc", "S1", "Windows 11");
    private static readonly DateTimeOffset T = DateTimeOffset.UtcNow.AddHours(-3);

    public ReportHostHealthEventsCommandHandlerTests()
    {
        _hosts.Setup(r => r.GetBySerialNumberAsync("S1", It.IsAny<CancellationToken>())).ReturnsAsync(_host);
        _events.Setup(r => r.GetHealthEventsForHostSinceAsync(_host.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _events.Setup(r => r.AddAsync(It.IsAny<HostEvent>(), It.IsAny<CancellationToken>()))
            .Callback<HostEvent, CancellationToken>((e, _) => _added.Add(e)).Returns(Task.CompletedTask);
    }

    private ReportHostHealthEventsCommandHandler Handler() => new(_hosts.Object, _events.Object, _unit.Object);

    private static HealthEventInput Input(string kind, double minutes = 0, string source = "Kernel-Power 41") =>
        new(T.AddMinutes(minutes), kind, source, "BugcheckCode 0");

    [Fact]
    public async Task RecordsTheEventsOnTheHostsTimeline()
    {
        var count = await Handler().Handle(
            new ReportHostHealthEventsCommand("S1", [Input("UnexpectedShutdown"), Input("HardwareErrorCorrected", 30, "WHEA-Logger 19")]),
            CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal([HostEventKind.UnexpectedShutdown, HostEventKind.HardwareErrorCorrected], _added.Select(e => e.Kind));
        Assert.Equal("Kernel-Power 41", _added[0].Subject);
        Assert.Equal("BugcheckCode 0", _added[0].Detail);
        Assert.All(_added, e => Assert.Null(e.Actor));
        _unit.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>One host cannot fill the table: past the cap nothing more is stored (it is already flagged
    /// as sending an unusual amount, which is how the flood is reported).</summary>
    [Fact]
    public async Task AHostThatIsAlreadyAtTheCapStoresNothingMore()
    {
        var full = Enumerable.Range(0, ReportHostHealthEventsCommandHandler.MaxStoredPer30Days)
            .Select(i => new HealthEventRecord(_host.Id, HostEventKind.HardwareErrorCorrected, DateTimeOffset.UtcNow.AddDays(-20).AddMinutes(i * 5)))
            .ToList();
        _events.Setup(r => r.GetHealthEventsForHostSinceAsync(_host.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(full);

        var count = await Handler().Handle(new ReportHostHealthEventsCommand("S1", [Input("UnexpectedShutdown")]), CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task OnlyTheRoomLeftUnderTheCapIsStored()
    {
        var almostFull = Enumerable.Range(0, ReportHostHealthEventsCommandHandler.MaxStoredPer30Days - 1)
            .Select(i => new HealthEventRecord(_host.Id, HostEventKind.HardwareErrorCorrected, DateTimeOffset.UtcNow.AddDays(-20).AddMinutes(i * 5)))
            .ToList();
        _events.Setup(r => r.GetHealthEventsForHostSinceAsync(_host.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(almostFull);

        var count = await Handler().Handle(
            new ReportHostHealthEventsCommand("S1", [Input("UnexpectedShutdown"), Input("UnexpectedShutdown", 30), Input("UnexpectedShutdown", 60)]),
            CancellationToken.None);

        Assert.Equal(1, count);
    }

    /// <summary>The agent retries, and the OS often logs one crash twice: the same thing is not two events.</summary>
    [Fact]
    public async Task AnEventTheServerAlreadyHasIsNotRecordedAgain()
    {
        _events.Setup(r => r.GetHealthEventsForHostSinceAsync(_host.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HealthEventRecord(_host.Id, HostEventKind.UnexpectedShutdown, T.AddSeconds(40))]);

        var count = await Handler().Handle(new ReportHostHealthEventsCommand("S1", [Input("UnexpectedShutdown")]), CancellationToken.None);

        Assert.Equal(0, count);
        _unit.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TwoOfTheSameKindWithinTheWindowInOneReportAreOne()
    {
        var count = await Handler().Handle(
            new ReportHostHealthEventsCommand("S1", [Input("UnexpectedShutdown"), Input("UnexpectedShutdown", 1, "BugCheck 1001")]),
            CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ADifferentKindAtTheSameTimeIsAnotherEvent()
    {
        var count = await Handler().Handle(
            new ReportHostHealthEventsCommand("S1", [Input("UnexpectedShutdown"), Input("HardwareErrorFatal", 0, "WHEA-Logger 1")]),
            CancellationToken.None);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task AnUnknownHostIsNotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Handler().Handle(new ReportHostHealthEventsCommand("NOPE", [Input("UnexpectedShutdown")]), CancellationToken.None));

    [Fact]
    public async Task AnEmptyReportDoesNothing()
    {
        Assert.Equal(0, await Handler().Handle(new ReportHostHealthEventsCommand("S1", []), CancellationToken.None));
        _unit.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}

public class ReportHostHealthEventsCommandValidatorTests
{
    private readonly ReportHostHealthEventsCommandValidator _validator = new();

    private static HealthEventInput Event(string kind = "UnexpectedShutdown", DateTimeOffset? at = null, string source = "Kernel-Power 41", string? detail = null) =>
        new(at ?? DateTimeOffset.UtcNow.AddHours(-1), kind, source, detail);

    private bool Valid(params HealthEventInput[] events) => _validator.Validate(new ReportHostHealthEventsCommand("S1", events)).IsValid;

    [Fact]
    public void AValidReportPasses() => Assert.True(Valid(Event(), Event("HardwareErrorFatal"), Event("HardwareErrorCorrected")));

    /// <summary>An agent can report health events and nothing else: it cannot write a "patched" or a
    /// "Patch now" line onto a timeline by choosing its kind.</summary>
    [Theory]
    [InlineData("ApplicationPatched")]
    [InlineData("PatchNowRequested")]
    [InlineData("FailureDismissed")]
    [InlineData("Nonsense")]
    public void OnlyTheThreeHealthKindsAreAccepted(string kind) => Assert.False(Valid(Event(kind)));

    [Fact]
    public void AnEventCannotBeInTheFutureOrOlderThan35Days()
    {
        Assert.False(Valid(Event(at: DateTimeOffset.UtcNow.AddHours(2))));
        Assert.False(Valid(Event(at: DateTimeOffset.UtcNow.AddDays(-36))));
        Assert.True(Valid(Event(at: DateTimeOffset.UtcNow.AddDays(-34))));
    }

    /// <summary>A body with no events at all is a bad request, not a crash.</summary>
    [Fact]
    public void AReportWithNoEventsListIsRefusedRatherThanCrashing() =>
        Assert.False(_validator.Validate(new ReportHostHealthEventsCommand("S1", null!)).IsValid);

    [Fact]
    public void ALongSourceOrDetailIsRefused()
    {
        Assert.False(Valid(Event(source: new string('x', 256))));
        Assert.False(Valid(Event(detail: new string('x', 1001))));
    }

    [Fact]
    public void OneReportIsCappedAt100Events() =>
        Assert.False(Valid(Enumerable.Range(0, 101).Select(_ => Event()).ToArray()));
}

public class DeviceHealthOnTheAutonomyPageTests
{
    private readonly Mock<IHostRepository> _hosts = new();
    private readonly Mock<IHostEventRepository> _events = new();
    private readonly Mock<IPatchFailureRepository> _failures = new();
    private readonly Mock<IUpgradePathRepository> _paths = new();
    private readonly Mock<IKpiSnapshotRepository> _snapshots = new();

    private GetAutonomyQueryHandler Handler(params Host[] hosts)
    {
        _hosts.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(hosts);
        _events.Setup(r => r.GetPatchActivitySinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _events.Setup(r => r.GetEventsSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _events.Setup(r => r.GetOldestEventUtcAsync(It.IsAny<CancellationToken>())).ReturnsAsync((DateTimeOffset?)null);
        _failures.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _paths.Setup(r => r.GetInstallationPatchStatusesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _snapshots.Setup(r => r.GetSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new GetAutonomyQueryHandler(_hosts.Object, _events.Object, _failures.Object, _paths.Object, _snapshots.Object);
    }

    private void GivenHealth(params HealthEventRecord[] events) =>
        _events.Setup(r => r.GetHealthEventsSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(events);

    [Fact]
    public async Task ListsTheUnhealthyDevicesWithTheirReasonAndCountsTheFleet()
    {
        var bad = new Host("bad-pc", "S1", "Windows 11");
        var meh = new Host("meh-mac", "S2", "macOS 15");
        var fine = new Host("fine-pc", "S3", "Windows 11");
        GivenHealth(
            new HealthEventRecord(bad.Id, HostEventKind.HardwareErrorFatal, DateTimeOffset.UtcNow.AddDays(-2)),
            new HealthEventRecord(meh.Id, HostEventKind.UnexpectedShutdown, DateTimeOffset.UtcNow.AddDays(-9)));

        var result = await Handler(bad, meh, fine).Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        Assert.Equal((1, 1), (result.DeviceHealth.Unstable, result.DeviceHealth.Watch));
        Assert.Equal(["bad-pc", "meh-mac"], result.DeviceHealth.Items.Select(i => i.Hostname));
        Assert.Equal(["Unstable", "Watch"], result.DeviceHealth.Items.Select(i => i.Status));
        Assert.Contains("fatal hardware error", result.DeviceHealth.Items[0].Why);
    }

    /// <summary>The page's size must not grow with the fleet: counts are the whole fleet's, rows are capped.</summary>
    [Fact]
    public async Task TheRowsAreCappedButTheCountsAreTheWholeFleets()
    {
        var hosts = Enumerable.Range(0, GetAutonomyQueryHandlerLimits.HealthRows + 10).Select(i => new Host($"pc-{i}", $"S{i}", "Windows 11")).ToArray();
        GivenHealth(hosts.Select(h => new HealthEventRecord(h.Id, HostEventKind.HardwareErrorFatal, DateTimeOffset.UtcNow.AddDays(-1))).ToArray());

        var result = await Handler(hosts).Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        Assert.Equal(hosts.Length, result.DeviceHealth.Unstable);
        Assert.Equal(GetAutonomyQueryHandlerLimits.HealthRows, result.DeviceHealth.Items.Count);
    }

    [Fact]
    public async Task TheUnstableCountIsAKeyFigureWithItsBand()
    {
        var bad = new Host("bad-pc", "S1", "Windows 11");
        GivenHealth(new HealthEventRecord(bad.Id, HostEventKind.HardwareErrorFatal, DateTimeOffset.UtcNow.AddDays(-1)));

        var result = await Handler(bad).Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        var kpi = result.Kpis.Single(k => k.Id == "health.unstable_devices");
        Assert.Equal(1, kpi.Value);
        Assert.False(kpi.IsPercent);
        Assert.Equal("A", kpi.Status);
    }

    [Fact]
    public async Task EventsOfAHostThatIsGoneAreIgnored()
    {
        var here = new Host("here", "S1", "Windows 11");
        GivenHealth(new HealthEventRecord(Guid.NewGuid(), HostEventKind.HardwareErrorFatal, DateTimeOffset.UtcNow.AddDays(-1)));

        var result = await Handler(here).Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        Assert.Equal(0, result.DeviceHealth.Unstable);
        Assert.Empty(result.DeviceHealth.Items);
    }
}
