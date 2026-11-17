using Moq;
using Kintsugi.Application.Autonomy;
using Kintsugi.Application.Autonomy.Commands.RecordKpiSnapshot;
using Kintsugi.Application.Autonomy.Commands.RequestAutonomyPatchNow;
using Kintsugi.Application.Autonomy.Queries.GetAutonomy;
using Kintsugi.Application.Autonomy.Queries.GetAutonomyCauseDevices;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.ForcedPatchRuns;
using Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;
using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Autonomy;

public class AutonomyPatchClassifierTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private readonly Guid _host = Guid.NewGuid();

    private PatchActivityRecord Rec(HostEventKind kind, string subject, double hoursFromT, Guid? host = null) =>
        new(host ?? _host, kind, subject, T.AddHours(hoursFromT));

    [Fact]
    public void APatchWithNoPatchNowBeforeIt_RanOnTheHostsOwnSchedule()
    {
        var result = AutonomyPatchClassifier.Classify([Rec(HostEventKind.ApplicationPatched, "Firefox", 0)]);

        Assert.False(Assert.Single(result).AdminStarted);
    }

    [Fact]
    public void APatchRightAfterThePickupOfARequestForTheSameThing_WasStartedByAnAdministrator()
    {
        var result = AutonomyPatchClassifier.Classify(
        [
            Rec(HostEventKind.PatchNowPickedUp, "Windows", 0),
            Rec(HostEventKind.OperatingSystemPatched, "Windows", 0.2),
        ]);

        Assert.True(Assert.Single(result).AdminStarted);
    }

    /// <summary>A forced run lives 24 hours, so a patch later than that cannot have been caused by it.</summary>
    [Fact]
    public void APatchMoreThanADayAfterThePickup_IsNotTheRequests()
    {
        var result = AutonomyPatchClassifier.Classify(
        [
            Rec(HostEventKind.PatchNowPickedUp, "Windows", 0),
            Rec(HostEventKind.OperatingSystemPatched, "Windows", 25),
        ]);

        Assert.False(Assert.Single(result).AdminStarted);
    }

    [Fact]
    public void APickupForOneThing_DoesNotMakeAPatchOfAnotherThingAdminStarted()
    {
        var result = AutonomyPatchClassifier.Classify(
        [
            Rec(HostEventKind.PatchNowPickedUp, "Windows", 0),
            Rec(HostEventKind.ApplicationPatched, "Firefox", 0.1),
        ]);

        Assert.False(Assert.Single(result).AdminStarted);
    }

    [Fact]
    public void APickupOnAnotherHost_DoesNotCount()
    {
        var other = Guid.NewGuid();
        var result = AutonomyPatchClassifier.Classify(
        [
            Rec(HostEventKind.PatchNowPickedUp, "Windows", 0, other),
            Rec(HostEventKind.OperatingSystemPatched, "Windows", 0.1),
        ]);

        Assert.False(Assert.Single(result).AdminStarted);
    }

    /// <summary>The next patch uses the request up: one press is never counted for two patches.</summary>
    [Fact]
    public void OnePickup_IsConsumedByTheFirstPatchAfterIt()
    {
        var result = AutonomyPatchClassifier.Classify(
        [
            Rec(HostEventKind.PatchNowPickedUp, "Windows", 0),
            Rec(HostEventKind.OperatingSystemPatched, "Windows", 0.1),
            Rec(HostEventKind.OperatingSystemPatched, "Windows", 2),
        ]).OrderBy(p => p.OccurredUtc).ToList();

        Assert.Equal([true, false], result.Select(p => p.AdminStarted));
    }

    [Fact]
    public void ThePickupAndThePatchAtTheSameInstant_CountAsTheRequests()
    {
        var result = AutonomyPatchClassifier.Classify(
        [
            Rec(HostEventKind.OperatingSystemPatched, "Windows", 0),
            Rec(HostEventKind.PatchNowPickedUp, "Windows", 0),
        ]);

        Assert.True(Assert.Single(result).AdminStarted);
    }

    [Fact]
    public void SubjectsMatchWhateverTheirCase()
    {
        var result = AutonomyPatchClassifier.Classify(
        [
            Rec(HostEventKind.PatchNowPickedUp, "SLACK", 0),
            Rec(HostEventKind.ApplicationPatched, "slack", 0.1),
        ]);

        Assert.True(Assert.Single(result).AdminStarted);
    }

    [Fact]
    public void FailuresAndOtherEvents_AreNotPatches()
    {
        var result = AutonomyPatchClassifier.Classify(
        [
            Rec(HostEventKind.PatchFailed, "Windows", 0),
            Rec(HostEventKind.PatchNowRequested, "Windows", 0),
        ]);

        Assert.Empty(result);
    }
}

public abstract class AutonomyFleetFixture
{
    protected readonly Mock<IHostRepository> Hosts = new();
    protected readonly Mock<IHostEventRepository> Events = new();
    protected readonly Mock<IPatchFailureRepository> Failures = new();
    protected readonly Mock<IUpgradePathRepository> Paths = new();
    protected readonly Mock<IKpiSnapshotRepository> Snapshots = new();

    protected readonly Host OnlineMac = new("online-mac", "S1", "macOS 15");
    protected readonly Host OfflineWindows = new("offline-pc", "S2", "Windows 11 Pro", operatingSystemUpdateAvailable: true);
    protected readonly Host OnlineWindows = new("online-pc", "S3", "Windows 11 Pro");

    protected AutonomyFleetFixture()
    {
        OnlineMac.RecordHeartbeat(HostStatus.Online);
        OnlineWindows.RecordHeartbeat(HostStatus.Online);

        // Last heard from five days ago: RecordHeartbeat only ever stamps "now", so the past is set
        // directly. A host's status is derived from this time, which is what makes it "disconnected".
        OfflineWindows.RecordHeartbeat(HostStatus.Online);
        typeof(Host).GetProperty(nameof(Host.LastSeenUtc))!.SetValue(OfflineWindows, DateTimeOffset.UtcNow.AddDays(-5));

        Hosts.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { OnlineMac, OfflineWindows, OnlineWindows });
        Events.Setup(r => r.GetPatchActivitySinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Events.Setup(r => r.GetEventsSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Events.Setup(r => r.GetOldestEventUtcAsync(It.IsAny<CancellationToken>())).ReturnsAsync((DateTimeOffset?)null);
        Events.Setup(r => r.GetHealthEventsSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Failures.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Paths.Setup(r => r.GetInstallationPatchStatusesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Snapshots.Setup(r => r.GetSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    protected PatchFailureDto Failure(Host h, string app, string details, int count, int daysAgo = 2) => new(
        Guid.NewGuid(), h.Id, h.Hostname, h.SerialNumber, app, null, null, null, details,
        DateTimeOffset.UtcNow.AddDays(-daysAgo), DateTimeOffset.UtcNow, count, PatchFailureResolution.Outstanding, null, false, false, UpgradeMethod.Script);
}

public class GetAutonomyQueryHandlerTests : AutonomyFleetFixture
{
    private GetAutonomyQueryHandler Handler() => new(Hosts.Object, Events.Object, Failures.Object, Paths.Object, Snapshots.Object);

    [Theory]
    [InlineData("7d", 7)]
    [InlineData("30d", 30)]
    public async Task ADailyPeriod_HasOneBarPerDay(string period, int days)
    {
        var result = await Handler().Handle(new GetAutonomyQuery(period), CancellationToken.None);

        Assert.Equal(days, result.Buckets.Count);
    }

    [Fact]
    public async Task APeriodWithNoHistory_ReportsZeroNotAnError()
    {
        var result = await Handler().Handle(new GetAutonomyQuery("30d"), CancellationToken.None);

        Assert.Equal(0, result.Patches.Total);
        Assert.Null(result.HistoryStartsUtc);
        Assert.Single(result.Months);
        Assert.Equal(3, result.TotalHosts);
    }

    [Fact]
    public async Task PatchesAreCountedBySchedulingAndAdminStarts()
    {
        var now = DateTimeOffset.UtcNow;
        Events.Setup(r => r.GetPatchActivitySinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new PatchActivityRecord(OnlineMac.Id, HostEventKind.ApplicationPatched, "Firefox", now.AddHours(-30)),
            new PatchActivityRecord(OnlineWindows.Id, HostEventKind.PatchNowPickedUp, "Windows", now.AddHours(-3)),
            new PatchActivityRecord(OnlineWindows.Id, HostEventKind.OperatingSystemPatched, "Windows", now.AddHours(-2)),
        ]);

        var result = await Handler().Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        Assert.Equal(new AutonomyPatchesDto(2, 1, 1), result.Patches);
        Assert.Equal(2, result.Buckets.Sum(b => b.Own + b.Admin));
    }

    /// <summary>The page lists causes, never devices: a fleet of 1,200 must not be sent to the browser.</summary>
    [Fact]
    public async Task CausesCarryCountsAndCost_NotDeviceLists()
    {
        var result = await Handler().Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        var disconnected = result.Causes.Single(c => c.Key == "disconnected");
        Assert.Equal(1, disconnected.Devices);
        Assert.Equal(5, disconnected.DeviceDays, 0.2);
        Assert.Equal(1, disconnected.PatchNowOffline);
        Assert.Equal(0, disconnected.PatchNowConnected);
    }

    [Fact]
    public async Task MaintenanceMode_IsReportedAsNotMeasured()
    {
        var result = await Handler().Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        var maintenance = result.Causes.Single(c => c.Key == "maintenance");
        Assert.False(maintenance.Measured);
        Assert.Equal(0, maintenance.Devices);
    }

    [Fact]
    public async Task ACauseShareIsItsPartOfAllTheDeviceDays()
    {
        OnlineMac.RecordRestartState(true, "linux-base", DateTimeOffset.UtcNow.AddDays(-5));

        var result = await Handler().Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        Assert.InRange(result.Causes.Sum(c => c.SharePercent), 99, 101);
        Assert.Equal(2, result.DevicesDelayed);
    }

    [Fact]
    public async Task FailuresAreGroupedByCauseAndCostedBySinceWhen()
    {
        Failures.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Failure(OnlineWindows, "Windows", "ERROR 0x80070643", 4, daysAgo: 6),
            Failure(OnlineMac, "Slack", "exit 1", 2, daysAgo: 2),
            Failure(OnlineMac, "Notion", "exit 1", 1, daysAgo: 1),
        ]);

        var result = await Handler().Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        var app = result.Causes.Single(c => c.Key == "failure:an-application-update-failed");
        Assert.Equal(2, app.Devices);
        Assert.Equal(3, app.DeviceDays, 0.2);
        Assert.Equal(0, app.PatchNowConnected);
        var windows = result.Causes.Single(c => c.Category == "Failure" && c.Key != app.Key);
        Assert.Equal(6, windows.DeviceDays, 0.2);
    }

    [Fact]
    public async Task AMonthShowsThatMonthsDays()
    {
        var result = await Handler().Handle(new GetAutonomyQuery("month", "2026-02"), CancellationToken.None);

        Assert.Equal("2026-02", result.Month);
        Assert.Equal(28, result.Buckets.Count);
    }

    [Fact]
    public async Task ABadOrFutureMonth_FallsBackToTheCurrentOne()
    {
        var current = DateTimeOffset.UtcNow.ToString("yyyy-MM");

        Assert.Equal(current, (await Handler().Handle(new GetAutonomyQuery("month", "nonsense"), CancellationToken.None)).Month);
        Assert.Equal(current, (await Handler().Handle(new GetAutonomyQuery("month", "2099-01"), CancellationToken.None)).Month);
    }

    [Theory]
    [InlineData("7d", true)]
    [InlineData("30d", true)]
    [InlineData("month", true)]
    [InlineData("90d", false)]
    [InlineData("", false)]
    public void TheValidator_AcceptsOnlyTheThreePeriods(string period, bool valid) =>
        Assert.Equal(valid, new GetAutonomyQueryValidator().Validate(new GetAutonomyQuery(period)).IsValid);
}

public class AutonomyKpiTests : AutonomyFleetFixture
{
    private GetAutonomyQueryHandler Handler() => new(Hosts.Object, Events.Object, Failures.Object, Paths.Object, Snapshots.Object);

    [Theory]
    [InlineData(KpiDirection.LowerIsBetter, 2, 5, 2, "G")]
    [InlineData(KpiDirection.LowerIsBetter, 2, 5, 5, "A")]
    [InlineData(KpiDirection.LowerIsBetter, 2, 5, 5.1, "R")]
    [InlineData(KpiDirection.HigherIsBetter, 98, 95, 98, "G")]
    [InlineData(KpiDirection.HigherIsBetter, 98, 95, 96, "A")]
    [InlineData(KpiDirection.HigherIsBetter, 98, 95, 94, "R")]
    [InlineData(KpiDirection.Information, 0, 0, 50, "I")]
    public void TheBandsAreEdgeInclusiveAndDirectional(KpiDirection direction, double green, double amber, double value, string expected) =>
        Assert.Equal(expected, new KpiDefinition("x", "c", "d", direction, green, amber, true).StatusOf(value));

    [Fact]
    public void EveryKpiHasAUniqueStableId() =>
        Assert.Equal(AutonomyKpis.All.Count, AutonomyKpis.All.Select(k => k.Id).Distinct().Count());

    [Fact]
    public async Task APercentKpiIsItsShareOfTheFleet()
    {
        Failures.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Failure(OnlineWindows, "Windows", "x", 1)]);

        var result = await Handler().Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        var kpi = result.Kpis.Single(k => k.Id == "failures.open_failure_devices");
        Assert.Equal((1, 3), (kpi.Numerator, kpi.Denominator));
        Assert.Equal(33.3, kpi.Value);
        Assert.Equal("R", kpi.Status);
    }

    [Fact]
    public async Task PriorAndChangeComeFromTheSnapshotAWeekAgoOrOlder()
    {
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-8);
        Snapshots.Setup(r => r.GetSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            KpiSnapshot.Record(weekAgo, "patching.os_update_waiting", 10, 3, 30),
            KpiSnapshot.Record(DateTimeOffset.UtcNow.AddDays(-2), "patching.os_update_waiting", 50, 15, 30),
        ]);

        var result = await Handler().Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        var kpi = result.Kpis.Single(k => k.Id == "patching.os_update_waiting");
        Assert.Equal(10, kpi.Prior);
        Assert.Equal(Math.Round(33.3 - 10, 1), kpi.Delta);
    }

    [Fact]
    public async Task WithNoSnapshotsThereIsNoPriorRatherThanAGuess()
    {
        var result = await Handler().Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        Assert.All(result.Kpis, k => Assert.Null(k.Prior));
        Assert.Empty(result.KpiHistory);
    }

    [Fact]
    public async Task TheAdminStartedShareIsInformationOnly()
    {
        var result = await Handler().Handle(new GetAutonomyQuery("7d"), CancellationToken.None);

        Assert.Equal("I", result.Kpis.Single(k => k.Id == AutonomyKpis.AdminStartedShare).Status);
    }
}

public class RecordKpiSnapshotCommandHandlerTests : AutonomyFleetFixture
{
    private readonly Mock<IUnitOfWork> _unit = new();

    private RecordKpiSnapshotCommandHandler Handler() => new(Hosts.Object, Failures.Object, Paths.Object, Events.Object, Snapshots.Object, _unit.Object);

    [Fact]
    public async Task WritesOneSnapshotPerKpiAndSaves()
    {
        IEnumerable<KpiSnapshot>? written = null;
        Snapshots.Setup(r => r.ExistsForDateAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        Snapshots.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<KpiSnapshot>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<KpiSnapshot>, CancellationToken>((s, _) => written = s.ToList())
            .Returns(Task.CompletedTask);

        var count = await Handler().Handle(new RecordKpiSnapshotCommand(), CancellationToken.None);

        Assert.Equal(AutonomyKpis.All.Count, count);
        Assert.Equal(AutonomyKpis.All.Select(k => k.Id).Order(), written!.Select(s => s.KpiId).Order());
        _unit.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A restart, or two instances, must not write a day twice.</summary>
    [Fact]
    public async Task DoesNothingWhenTodayAlreadyHasItsSnapshot()
    {
        Snapshots.Setup(r => r.ExistsForDateAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var count = await Handler().Handle(new RecordKpiSnapshotCommand(), CancellationToken.None);

        Assert.Equal(0, count);
        Snapshots.Verify(r => r.AddRangeAsync(It.IsAny<IEnumerable<KpiSnapshot>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DoesNothingForAnEmptyFleetRatherThanRecordingZeros()
    {
        Snapshots.Setup(r => r.ExistsForDateAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        Hosts.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Host>());

        Assert.Equal(0, await Handler().Handle(new RecordKpiSnapshotCommand(), CancellationToken.None));
    }
}

public class AutonomyCauseDevicesAndPatchNowTests : AutonomyFleetFixture
{
    private readonly Mock<ISender> _sender = new();

    private GetAutonomyCauseDevicesQueryHandler Devices() => new(Hosts.Object, Failures.Object, Paths.Object);

    private RequestAutonomyPatchNowCommandHandler PatchNow() => new(Hosts.Object, Failures.Object, Paths.Object, _sender.Object);

    private void GivenWindowsFailures()
    {
        // Two Windows devices failing the same Windows update: one connected, one not.
        Failures.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Failure(OnlineWindows, "Windows", "ERROR 0x80070643", 4),
            Failure(OfflineWindows, "Windows", "ERROR 0x80070643", 2),
        ]);
        var cause = "failure:" + AutonomyCauseAnalyzer.Slug(Failures.Object.GetAllAsync(default).Result[0].CauseTitle ?? "An application update failed");
        _key = cause;
    }

    private string _key = "";

    [Fact]
    public async Task DevicesComePagedAndSearchable()
    {
        var page = await Devices().Handle(new GetAutonomyCauseDevicesQuery("disconnected", 0, 10, "offline"), CancellationToken.None);

        Assert.Equal(1, page.Total);
        Assert.Equal("offline-pc", page.Items.Single().Hostname);
        var none = await Devices().Handle(new GetAutonomyCauseDevicesQuery("disconnected", 0, 10, "zzz"), CancellationToken.None);
        Assert.Equal(0, none.Total);
    }

    [Fact]
    public async Task AnUnknownCauseIsNotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Devices().Handle(new GetAutonomyCauseDevicesQuery("nonsense"), CancellationToken.None));

    [Fact]
    public void TheDevicesValidator_CapsAPageSoOneCallCannotReadAFleet()
    {
        var v = new GetAutonomyCauseDevicesQueryValidator();

        Assert.True(v.Validate(new GetAutonomyCauseDevicesQuery("k", 0, 200)).IsValid);
        Assert.False(v.Validate(new GetAutonomyCauseDevicesQuery("k", 0, 201)).IsValid);
        Assert.False(v.Validate(new GetAutonomyCauseDevicesQuery("k", -1, 10)).IsValid);
    }

    [Fact]
    public async Task PatchNow_SendsTheConnectedWindowsDevicesByNameWithTheAdministrator()
    {
        GivenWindowsFailures();
        _sender.Setup(s => s.Send(It.IsAny<RequestForcedPatchRunsCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RequestForcedPatchRunsResult(1, []));

        var result = await PatchNow().Handle(new RequestAutonomyPatchNowCommand(_key, 1, false, "sara@example.com"), CancellationToken.None);

        Assert.Equal(1, result.Started);
        Assert.Equal(0, result.QueuedOffline);
        _sender.Verify(s => s.Send(
            It.Is<RequestForcedPatchRunsCommand>(c => c.ApplicationName == "Windows" && c.Platform == "Windows"
                && c.HostNames.SequenceEqual(new[] { "online-pc" }) && c.RequestedBy == "sara@example.com"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PatchNow_QueuesTheOfflineOnesOnlyWhenAsked()
    {
        GivenWindowsFailures();
        _sender.Setup(s => s.Send(It.IsAny<RequestForcedPatchRunsCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RequestForcedPatchRunsResult(2, []));

        var result = await PatchNow().Handle(new RequestAutonomyPatchNowCommand(_key, 1, true, "sara@example.com"), CancellationToken.None);

        Assert.Equal((1, 1), (result.Started, result.QueuedOffline));
        _sender.Verify(s => s.Send(
            It.Is<RequestForcedPatchRunsCommand>(c => c.HostNames.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The list moves while the dialog is open. A different count than the one shown is refused.</summary>
    [Fact]
    public async Task PatchNow_IsRefusedWhenTheCountTheAdministratorSawNoLongerHolds()
    {
        GivenWindowsFailures();

        await Assert.ThrowsAsync<ConflictException>(() =>
            PatchNow().Handle(new RequestAutonomyPatchNowCommand(_key, 5, false, "sara@example.com"), CancellationToken.None));
        _sender.Verify(s => s.Send(It.IsAny<RequestForcedPatchRunsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Patch now installs and restarts only if Windows asks. It does not restart a host that
    /// already installed everything, so a restart-pending host is never offered it.</summary>
    [Fact]
    public async Task PatchNow_NeverActsOnARestartPendingDevice()
    {
        OnlineWindows.RecordRestartState(true, "x", DateTimeOffset.UtcNow.AddDays(-3));

        var result = await PatchNow().Handle(new RequestAutonomyPatchNowCommand("restart", 0, true, "sara@example.com"), CancellationToken.None);

        Assert.Equal((0, 0), (result.Started, result.QueuedOffline));
        _sender.Verify(s => s.Send(It.IsAny<RequestForcedPatchRunsCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PatchNow_NeverActsOnAMacOrAnApplicationFailure()
    {
        Failures.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Failure(OnlineMac, "Slack", "exit 1", 2)]);

        var result = await PatchNow().Handle(
            new RequestAutonomyPatchNowCommand("failure:an-application-update-failed", 0, true, "sara@example.com"), CancellationToken.None);

        Assert.Equal((0, 0), (result.Started, result.QueuedOffline));
    }

    [Fact]
    public async Task PatchNow_ForAnUnknownCauseIsNotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() =>
            PatchNow().Handle(new RequestAutonomyPatchNowCommand("nonsense", 0, false), CancellationToken.None));

    [Theory]
    [InlineData(5 * 24 + 3, "5 days")]
    [InlineData(24, "1 day")]
    [InlineData(9, "9 hours")]
    [InlineData(1, "1 hour")]
    public void Ago_ReadsInTheLargestWholeUnit(int hours, string expected)
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(expected, AutonomyCauseAnalyzer.Ago(now.AddHours(-hours), now));
    }

    [Fact]
    public void Ago_IsNullForATimeNeverReported() => Assert.Null(AutonomyCauseAnalyzer.Ago(null, DateTimeOffset.UtcNow));

    [Fact]
    public void Slug_MakesAStableKeyFromACauseTitle() =>
        Assert.Equal("windows-update-error-0x80070643", AutonomyCauseAnalyzer.Slug("Windows Update error 0x80070643"));
}
