using Moq;
using Kintsugi.Application.Applications.Commands.ReportPatchResult;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ForcedPatchRuns.Commands.ClaimForcedPatchRuns;
using Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;
using Kintsugi.Application.HostActivity;
using Kintsugi.Application.HostActivity.Commands.UpdateHostActivityRetention;
using Kintsugi.Application.HostActivity.Queries.GetHostActivity;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Application.PatchFailures.Commands.DismissPatchFailure;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.HostActivity;

public class HostEventTests
{
    [Fact]
    public void Record_KeepsShortDetailAsIs()
    {
        var e = HostEvent.Record(Guid.NewGuid(), HostEventKind.PatchFailed, "Windows", DateTimeOffset.UtcNow, detail: "exit 1");

        Assert.Equal("exit 1", e.Detail);
        Assert.Equal(1, e.Count);
    }

    /// <summary>A failing install's output can be megabytes; the table keeps the start and the end.</summary>
    [Fact]
    public void Record_CutsLongDetailKeepingBothEnds()
    {
        var text = "START" + new string('x', 20_000) + "END";

        var e = HostEvent.Record(Guid.NewGuid(), HostEventKind.PatchFailed, "Windows", DateTimeOffset.UtcNow, detail: text);

        Assert.True(e.Detail!.Length <= HostEvent.MaxDetailLength);
        Assert.StartsWith("START", e.Detail);
        Assert.EndsWith("END", e.Detail);
        Assert.Contains("output cut", e.Detail);
    }

    [Fact]
    public void RecordRepeat_CountsAndMovesToTheLatestTry()
    {
        var first = DateTimeOffset.UtcNow.AddHours(-3);
        var e = HostEvent.Record(Guid.NewGuid(), HostEventKind.PatchFailed, "Windows", first, detail: "try 1");

        var second = DateTimeOffset.UtcNow;
        e.RecordRepeat(second, "try 2", "1", "2");

        Assert.Equal(2, e.Count);
        Assert.Equal(second, e.OccurredUtc);
        Assert.Equal("try 2", e.Detail);
    }
}

public class HostEventRecorderTests
{
    private readonly Mock<IHostEventRepository> _repository = new();
    private readonly List<HostEvent> _added = [];
    private readonly Guid _hostId = Guid.NewGuid();

    public HostEventRecorderTests()
    {
        _repository
            .Setup(r => r.AddAsync(It.IsAny<HostEvent>(), It.IsAny<CancellationToken>()))
            .Callback<HostEvent, CancellationToken>((e, _) => _added.Add(e))
            .Returns(Task.CompletedTask);
    }

    private HostEventRecorder Recorder() => new(_repository.Object);

    private void GivenLatest(HostEvent? latest) =>
        _repository.Setup(r => r.GetLatestForSubjectAsync(_hostId, "Windows", It.IsAny<CancellationToken>())).ReturnsAsync(latest);

    [Fact]
    public async Task PatchFailed_FirstFailureAddsAnEvent()
    {
        GivenLatest(null);

        await Recorder().PatchFailedAsync(_hostId, "Windows", null, null, "boom", DateTimeOffset.UtcNow, CancellationToken.None);

        var e = Assert.Single(_added);
        Assert.Equal(HostEventKind.PatchFailed, e.Kind);
        Assert.Null(e.Actor);
    }

    /// <summary>A host failing the same install every cycle must not write a row per cycle.</summary>
    [Fact]
    public async Task PatchFailed_SameFailureAgainFoldsIntoTheLatestEvent()
    {
        var latest = HostEvent.Record(_hostId, HostEventKind.PatchFailed, "Windows", DateTimeOffset.UtcNow.AddHours(-1), detail: "try 1");
        GivenLatest(latest);

        await Recorder().PatchFailedAsync(_hostId, "Windows", null, null, "try 2", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(_added);
        Assert.Equal(2, latest.Count);
        Assert.Equal("try 2", latest.Detail);
    }

    /// <summary>A success in between ends the run: the next failure is a new event, not try 3.</summary>
    [Fact]
    public async Task PatchFailed_AfterASuccessStartsANewEvent()
    {
        GivenLatest(HostEvent.Record(_hostId, HostEventKind.OperatingSystemPatched, "Windows", DateTimeOffset.UtcNow.AddHours(-1)));

        await Recorder().PatchFailedAsync(_hostId, "Windows", null, null, "boom", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Single(_added);
    }

    [Fact]
    public async Task PatchNowRequested_NamesWhoPressedIt()
    {
        await Recorder().PatchNowRequestedAsync(_hostId, "Windows", "sara@example.com", DateTimeOffset.UtcNow, CancellationToken.None);

        var e = Assert.Single(_added);
        Assert.Equal(HostEventKind.PatchNowRequested, e.Kind);
        Assert.Equal("sara@example.com", e.Actor);
    }
}

public class HostActivityWiringTests
{
    private readonly Mock<IHostEventRecorder> _events = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IHostRepository> _hosts = new();

    [Fact]
    public async Task RequestingPatchNow_RecordsTheSignedInAdministrator()
    {
        var host = new Host("studio", "S1", "Windows 11");
        _hosts.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { host });
        var handler = new RequestForcedPatchRunsCommandHandler(_hosts.Object, new Mock<IForcedPatchRunRepository>().Object, _unitOfWork.Object, _events.Object);

        await handler.Handle(new RequestForcedPatchRunsCommand("Windows", "Windows", ["studio"], "juan@example.com"), CancellationToken.None);

        _events.Verify(e => e.PatchNowRequestedAsync(host.Id, "Windows", "juan@example.com", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClaimingARun_RecordsThatTheHostPickedItUp()
    {
        var host = new Host("studio", "S1", "Windows 11");
        _hosts.Setup(r => r.GetBySerialNumberAsync("S1", It.IsAny<CancellationToken>())).ReturnsAsync(host);
        var run = ForcedPatchRun.Request(host.Id, "Windows", "Windows", DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        var runs = new Mock<IForcedPatchRunRepository>();
        runs.Setup(r => r.GetCollectableForHostAsync(host.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([run]);
        var handler = new ClaimForcedPatchRunsCommandHandler(_hosts.Object, runs.Object, _unitOfWork.Object, _events.Object);

        await handler.Handle(new ClaimForcedPatchRunsCommand("S1"), CancellationToken.None);

        _events.Verify(e => e.PatchNowPickedUpAsync(host.Id, "Windows", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The timeline is the record that the host patched, so the event is written even when this
    /// server no longer tracks the application row, and the change is saved.
    /// </summary>
    [Fact]
    public async Task ReportingAPatch_RecordsItAndSavesEvenWhenTheApplicationIsNotTracked()
    {
        var host = new Host("studio", "S1", "macOS 15");
        _hosts.Setup(r => r.GetBySerialNumberAsync("S1", It.IsAny<CancellationToken>())).ReturnsAsync(host);
        var failures = new Mock<IPatchFailureRepository>();
        failures.Setup(r => r.GetOutstandingForApplicationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var apps = new Mock<IInstalledApplicationRepository>();
        var handler = new ReportPatchResultCommandHandler(_hosts.Object, apps.Object, failures.Object, _unitOfWork.Object, _events.Object);

        await handler.Handle(new ReportPatchResultCommand("S1", "Firefox", "132.0"), CancellationToken.None);

        _events.Verify(e => e.ApplicationPatchedAsync(host.Id, "Firefox", null, "132.0", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DismissingAFailure_RecordsWhoDidIt()
    {
        var hostId = Guid.NewGuid();
        var failure = PatchFailure.Open(hostId, "7-Zip", "Windows", "1", "2", "nope", DateTimeOffset.UtcNow);
        var repo = new Mock<IPatchFailureRepository>();
        repo.Setup(r => r.GetByIdAsync(failure.Id, It.IsAny<CancellationToken>())).ReturnsAsync(failure);
        var handler = new DismissPatchFailureCommandHandler(repo.Object, _unitOfWork.Object, _events.Object);

        await handler.Handle(new DismissPatchFailureCommand(failure.Id, "sara@example.com"), CancellationToken.None);

        _events.Verify(e => e.FailureDismissedAsync(hostId, "7-Zip", "sara@example.com", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class GetHostActivityQueryHandlerTests
{
    private readonly Mock<IHostRepository> _hosts = new();
    private readonly Mock<IHostEventRepository> _events = new();
    private readonly Mock<IHostActivitySettingsRepository> _settings = new();
    private readonly Mock<IInstalledApplicationRepository> _apps = new();
    private readonly Mock<IPatchFailureRepository> _failures = new();
    private readonly Host _host = new("studio", "S1", "Windows 11 Pro", operatingSystemUpdateAvailable: true, operatingSystemLatestVersion: "26300.1742");

    public GetHostActivityQueryHandlerTests()
    {
        _hosts.Setup(r => r.GetByIdAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_host);
        _events.Setup(r => r.GetForHostAsync(_host.Id, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _apps.Setup(r => r.GetByHostIdAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _failures.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    private GetHostActivityQueryHandler Handler() => new(_hosts.Object, _events.Object, _settings.Object, _apps.Object, _failures.Object);

    [Fact]
    public async Task UnknownHost_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Handler().Handle(new GetHostActivityQuery(Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task RetentionDefaultsToSixMonthsUntilAnAdminChangesIt()
    {
        var result = await Handler().Handle(new GetHostActivityQuery(_host.Id), CancellationToken.None);

        Assert.Equal(6, result.RetentionMonths);
    }

    [Fact]
    public async Task PendingUpdates_ListTheOperatingSystemAndApplicationsWithUpdatesAndMarkFailures()
    {
        var firefox = new InstalledApplication(_host.Id, "Firefox", "131.0", updateAvailable: true);
        var vlc = new InstalledApplication(_host.Id, "VLC", "3.0.20", updateAvailable: false);
        _apps.Setup(r => r.GetByHostIdAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync([firefox, vlc]);
        var failure = new PatchFailureDto(
            Guid.NewGuid(), _host.Id, "studio", "S1", "Windows", "Windows", null, null, "0x80070643",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 2, PatchFailureResolution.Outstanding, null, false, false, UpgradeMethod.Script);
        _failures.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([failure]);

        var result = await Handler().Handle(new GetHostActivityQuery(_host.Id), CancellationToken.None);

        Assert.Equal(["Windows", "Firefox"], result.PendingUpdates.Select(p => p.Name));
        Assert.True(result.PendingUpdates[0].Failed);
        Assert.Equal("26300.1742", result.PendingUpdates[0].AvailableVersion);
        Assert.False(result.PendingUpdates[1].Failed);
        Assert.Single(result.OpenFailures);
    }

    [Fact]
    public async Task LastPatched_AndTheThirtyDayCount_AreDerivedFromPatchEventsOnly()
    {
        var now = DateTimeOffset.UtcNow;
        _events.Setup(r => r.GetForHostAsync(_host.Id, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            HostEvent.Record(_host.Id, HostEventKind.PatchFailed, "Windows", now),
            HostEvent.Record(_host.Id, HostEventKind.ApplicationPatched, "Firefox", now.AddDays(-2)),
            HostEvent.Record(_host.Id, HostEventKind.OperatingSystemPatched, "Windows", now.AddDays(-40)),
        ]);

        var result = await Handler().Handle(new GetHostActivityQuery(_host.Id), CancellationToken.None);

        Assert.Equal(now.AddDays(-2), result.LastPatchedUtc);
        Assert.Equal(1, result.PatchedLast30Days);
    }

    [Theory]
    [InlineData("Windows 11 Pro 26H2 (26300)", "Windows")]
    [InlineData("macOS 15.2", "macOS")]
    [InlineData("Ubuntu 24.04.1 LTS", "Linux")]
    [InlineData(null, "Operating system")]
    public void OperatingSystemName_MatchesTheNamesTheFailureQueueUses(string? os, string expected) =>
        Assert.Equal(expected, GetHostActivityQueryHandler.OperatingSystemName(os));
}

public class HostActivityRetentionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Validator_RejectsOutOfRange(int months) =>
        Assert.False(new UpdateHostActivityRetentionCommandValidator().Validate(new UpdateHostActivityRetentionCommand(months)).IsValid);

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(60)]
    public void Validator_AcceptsInRange(int months) =>
        Assert.True(new UpdateHostActivityRetentionCommandValidator().Validate(new UpdateHostActivityRetentionCommand(months)).IsValid);

    [Fact]
    public void Settings_RejectOutOfRange() =>
        Assert.Throws<DomainException>(() => HostActivitySettings.Create(0));

    [Fact]
    public async Task Handler_CreatesTheSettingsRowTheFirstTime()
    {
        var repo = new Mock<IHostActivitySettingsRepository>();
        var unit = new Mock<IUnitOfWork>();

        var result = await new UpdateHostActivityRetentionCommandHandler(repo.Object, unit.Object)
            .Handle(new UpdateHostActivityRetentionCommand(12), CancellationToken.None);

        Assert.Equal(12, result.RetentionMonths);
        repo.Verify(r => r.AddAsync(It.Is<HostActivitySettings>(s => s.RetentionMonths == 12), It.IsAny<CancellationToken>()), Times.Once);
        unit.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pruner_DeletesEventsOlderThanTheConfiguredMonths()
    {
        var events = new Mock<IHostEventRepository>();
        var settings = new Mock<IHostActivitySettingsRepository>();
        settings.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(HostActivitySettings.Create(3));
        var now = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        await new HostEventPruner(events.Object, settings.Object).PruneAsync(now, CancellationToken.None);

        events.Verify(r => r.DeleteOlderThanAsync(now.AddMonths(-3), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pruner_UsesSixMonthsWhenNothingWasConfigured()
    {
        var events = new Mock<IHostEventRepository>();
        var settings = new Mock<IHostActivitySettingsRepository>();
        var now = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

        await new HostEventPruner(events.Object, settings.Object).PruneAsync(now, CancellationToken.None);

        events.Verify(r => r.DeleteOlderThanAsync(now.AddMonths(-6), It.IsAny<CancellationToken>()), Times.Once);
    }
}
