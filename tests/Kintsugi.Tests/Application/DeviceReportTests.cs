using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.DeviceHealth;
using Kintsugi.Application.DeviceReport;
using Kintsugi.Application.HostActivity;
using Kintsugi.Application.LogCollections;
using Kintsugi.Application.LogCollections.Commands;
using Kintsugi.Application.LogCollections.Queries;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application;

public class LogCollectionEntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static LogCollection New(int days = 30) => LogCollection.Request(Guid.NewGuid(), "juan@htw.com.au", days, LogCollectionDetail.WarningsAndErrors, Now);

    [Fact]
    public void ARequestWaitsForTheDevice_AndExpiresAfter24Hours()
    {
        var c = New();

        Assert.Equal(LogCollectionStatus.Requested, c.EffectiveStatusAt(Now.AddHours(23)));
        Assert.Equal(LogCollectionStatus.Failed, c.EffectiveStatusAt(Now.AddHours(25)));
        Assert.Contains("24 hours", LogCollection.EffectiveFailureReason(c.Status, c.FailureReason, c.ExpiresUtc, c.ClaimedUtc, Now.AddHours(25)));
    }

    [Fact]
    public void APickUpThatNeverAnswers_IsFailedAfter30Minutes()
    {
        var c = New();
        c.MarkClaimed(Now.AddHours(1));

        Assert.Equal(LogCollectionStatus.Collecting, c.EffectiveStatusAt(Now.AddHours(1).AddMinutes(29)));
        Assert.Equal(LogCollectionStatus.Failed, c.EffectiveStatusAt(Now.AddHours(1).AddMinutes(31)));
    }

    [Fact]
    public void OnlyAPickedUpRequestCanBeCompleted_AndAnExpiredOneCannotBePickedUp()
    {
        var c = New();
        Assert.Throws<DomainException>(() => c.Complete(Now, [1], 1, null));
        Assert.Throws<DomainException>(() => c.MarkClaimed(Now.AddHours(25)));

        c.MarkClaimed(Now.AddMinutes(5));
        c.Complete(Now.AddMinutes(9), [1, 2, 3], 7, Now.AddDays(-3));

        Assert.Equal((LogCollectionStatus.Completed, 7, 3L), (c.Status, c.EntryCount, c.SizeBytes));
        Assert.Equal(Now.AddMinutes(9) + LogCollection.Retention, c.KeepUntilUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(365)]
    public void OnlySevenOrThirtyDaysCanBeAsked(int days) => Assert.Throws<DomainException>(() => New(days));
}

public class LogPayloadTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Gzip(string json)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zip.Write(Encoding.UTF8.GetBytes(json));
        }

        return output.ToArray();
    }

    private static string Entry(DateTimeOffset t, string sev = "Error", string src = "WHEA-Logger 18", string msg = "A fatal hardware error has occurred.") =>
        JsonSerializer.Serialize(new { t, sev, src, msg });

    private static byte[] Upload(IEnumerable<string> entries, string truncatedBefore = "null") =>
        Gzip($"{{\"entries\":[{string.Join(",", entries)}],\"truncatedBefore\":{truncatedBefore}}}");

    [Fact]
    public void AGoodUploadIsKeptAndCanBeReadBack()
    {
        var result = LogPayload.Sanitise(Upload([Entry(Now.AddHours(-2)), Entry(Now.AddHours(-1), "Warning", "disk 153", "retried")]), 30, Now);

        var read = LogPayload.Read(result.Content);
        Assert.Equal(2, result.Count);
        Assert.Equal(["disk 153", "WHEA-Logger 18"], read.OrderByDescending(e => e.T).Select(e => e.Src));
        Assert.Null(result.TruncatedBefore);
    }

    [Fact]
    public void EntriesWithABadSeverityOrAnImpossibleTimeAreDropped()
    {
        var result = LogPayload.Sanitise(Upload([
            Entry(Now.AddHours(-1)), Entry(Now.AddHours(-1), "Debug"), Entry(Now.AddDays(-90)), Entry(Now.AddDays(2)), Entry(Now.AddHours(-1), "")]), 30, Now);

        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void LongTextIsCutToItsLimits()
    {
        var result = LogPayload.Sanitise(Upload([Entry(Now.AddHours(-1), src: new string('s', 500), msg: new string('m', 5000))]), 30, Now);

        var e = LogPayload.Read(result.Content).Single();
        Assert.True(e.Src.Length <= LogPayload.MaxSourceLength + 1);
        Assert.True(e.Msg.Length <= LogPayload.MaxMessageLength + 1);
    }

    [Fact]
    public void OnlyTheNewestEntriesAreKept()
    {
        var many = Enumerable.Range(0, LogPayload.MaxEntries + 50).Select(i => Entry(Now.AddMinutes(-i - 1)));

        var result = LogPayload.Sanitise(Upload(many), 30, Now);

        Assert.Equal(LogPayload.MaxEntries, result.Count);
        Assert.DoesNotContain(LogPayload.Read(result.Content), e => e.T < Now.AddMinutes(-LogPayload.MaxEntries - 1));
    }

    [Fact]
    public void TheTruncationTimeIsKept()
    {
        var result = LogPayload.Sanitise(Upload([Entry(Now.AddHours(-1))], "\"2026-10-24T00:00:00Z\""), 30, Now);

        Assert.Equal(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), result.TruncatedBefore);
    }

    [Fact]
    public void GarbageEmptyAndOversizedUploadsAreRefused()
    {
        Assert.Throws<DomainException>(() => LogPayload.Sanitise([1, 2, 3, 4], 30, Now));
        Assert.Throws<DomainException>(() => LogPayload.Sanitise([], 30, Now));
        Assert.Throws<DomainException>(() => LogPayload.Sanitise(Gzip("not json"), 30, Now));
        Assert.Throws<DomainException>(() => LogPayload.Sanitise(new byte[LogPayload.MaxCompressedBytes + 1], 30, Now));
    }

    /// <summary>A few kilobytes that expand to a huge stream must not be expanded into memory.</summary>
    [Fact]
    public void AnUploadThatExpandsTooFarIsRefused()
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)'a');
            for (var i = 0; i < 70; i++)
            {
                zip.Write(chunk);
            }
        }

        Assert.True(output.Length < LogPayload.MaxCompressedBytes);
        Assert.Throws<DomainException>(() => LogPayload.Sanitise(output.ToArray(), 30, Now));
    }

    [Fact]
    public void FilteringBySeverityTextAndTime()
    {
        var entries = new[]
        {
            new LogEntryDto(Now, "Critical", "Kernel-Power 41", "rebooted"),
            new LogEntryDto(Now.AddMinutes(-3), "Warning", "disk 153", "retried"),
            new LogEntryDto(Now.AddHours(-5), "Error", "WHEA-Logger 18", "fatal"),
        };

        Assert.Equal(2, GetLogEntriesQueryHandler.Filter(entries, "errors", null, null, 10).Count());
        Assert.Single(GetLogEntriesQueryHandler.Filter(entries, "warnings", null, null, 10));
        Assert.Single(GetLogEntriesQueryHandler.Filter(entries, null, "WHEA", null, 10));
        Assert.Equal(2, GetLogEntriesQueryHandler.Filter(entries, null, null, Now, 10).Count());
    }
}

public class LogCollectionHandlerTests
{
    private readonly Mock<IHostRepository> _hosts = new();
    private readonly Mock<ILogCollectionRepository> _collections = new();
    private readonly Mock<IHostEventRecorder> _events = new();
    private readonly Mock<IUnitOfWork> _unit = new();
    private readonly Host _host = new("pc", "S1", "Windows 11");
    private readonly Host _other = new("other", "S2", "Windows 11");

    public LogCollectionHandlerTests()
    {
        _hosts.Setup(h => h.GetByIdAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_host);
        _hosts.Setup(h => h.GetBySerialNumberAsync("S1", It.IsAny<CancellationToken>())).ReturnsAsync(_host);
        _hosts.Setup(h => h.GetBySerialNumberAsync("S2", It.IsAny<CancellationToken>())).ReturnsAsync(_other);
    }

    private static byte[] Upload() =>
        Gz($"{{\"entries\":[{{\"t\":\"{DateTimeOffset.UtcNow.AddHours(-1):O}\",\"sev\":\"Error\",\"src\":\"x\",\"msg\":\"y\"}}],\"truncatedBefore\":null}}");

    private static byte[] Gz(string json)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zip.Write(Encoding.UTF8.GetBytes(json));
        }

        return output.ToArray();
    }

    private LogCollection Claimed()
    {
        var c = LogCollection.Request(_host.Id, "a", 30, LogCollectionDetail.WarningsAndErrors, DateTimeOffset.UtcNow);
        c.MarkClaimed(DateTimeOffset.UtcNow);
        _collections.Setup(r => r.GetByIdAsync(c.Id, It.IsAny<CancellationToken>())).ReturnsAsync(c);
        return c;
    }

    [Fact]
    public async Task AdministratorsRequestLogs_AndItIsOnTheTimelineWithTheirName()
    {
        var dto = await new RequestLogCollectionCommandHandler(_hosts.Object, _collections.Object, _events.Object, _unit.Object)
            .Handle(new RequestLogCollectionCommand(_host.Id, 30, "WarningsAndErrors", "juan@htw.com.au"), CancellationToken.None);

        Assert.Equal(("Requested", 30, "juan@htw.com.au"), (dto.Status, dto.PeriodDays, dto.RequestedBy));
        _collections.Verify(r => r.AddAsync(It.IsAny<LogCollection>(), It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.LogsRequestedAsync(_host.Id, "juan@htw.com.au", It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _unit.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ASecondRequestWhileOneIsOpenIsRefused()
    {
        _collections.Setup(r => r.GetOpenForHostAsync(_host.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LogCollection.Request(_host.Id, "a", 7, LogCollectionDetail.Everything, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<ConflictException>(() =>
            new RequestLogCollectionCommandHandler(_hosts.Object, _collections.Object, _events.Object, _unit.Object)
                .Handle(new RequestLogCollectionCommand(_host.Id, 30, "Everything", "a"), CancellationToken.None));
    }

    [Fact]
    public async Task AnOldRequestNobodyPickedUpDoesNotBlockANewOne()
    {
        _collections.Setup(r => r.GetOpenForHostAsync(_host.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LogCollection.Request(_host.Id, "a", 7, LogCollectionDetail.Everything, DateTimeOffset.UtcNow.AddHours(-30)));

        var dto = await new RequestLogCollectionCommandHandler(_hosts.Object, _collections.Object, _events.Object, _unit.Object)
            .Handle(new RequestLogCollectionCommand(_host.Id, 30, "Everything", "a"), CancellationToken.None);

        Assert.Equal("Requested", dto.Status);
    }

    [Fact]
    public async Task TheAgentIsHandedTheRequestOnce()
    {
        var c = LogCollection.Request(_host.Id, "a", 7, LogCollectionDetail.Everything, DateTimeOffset.UtcNow);
        _collections.Setup(r => r.GetCollectableForHostAsync(_host.Id, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(c);

        var handler = new ClaimLogCollectionRequestsCommandHandler(_hosts.Object, _collections.Object, _unit.Object);
        var handed = await handler.Handle(new ClaimLogCollectionRequestsCommand("S1"), CancellationToken.None);

        Assert.Equal(new LogCollectionRequestDto(c.Id, 7, "Everything"), handed.Single());
        Assert.Equal(LogCollectionStatus.Collecting, c.Status);
        Assert.Empty(await handler.Handle(new ClaimLogCollectionRequestsCommand("UNKNOWN"), CancellationToken.None));
    }

    [Fact]
    public async Task ACompletedLogIsStoredAndOnTheTimeline()
    {
        var c = Claimed();

        var kept = await new CompleteLogCollectionCommandHandler(_hosts.Object, _collections.Object, _events.Object, _unit.Object)
            .Handle(new CompleteLogCollectionCommand("S1", c.Id, Upload()), CancellationToken.None);

        Assert.Equal(1, kept);
        Assert.Equal(LogCollectionStatus.Completed, c.Status);
        _events.Verify(e => e.LogsCollectedAsync(_host.Id, It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The id alone is not enough: one host's certificate cannot write another host's log.</summary>
    /// <summary>A finished collection must refuse a repeated upload before it is unzipped: Sanitise can expand 64 MB, and
    /// the garbage bytes here would otherwise fail with the gzip message instead of the status one.</summary>
    [Fact]
    public async Task ARepeatedUploadAgainstAFinishedCollectionIsRefusedBeforeItIsParsed()
    {
        var c = Claimed();
        var handler = new CompleteLogCollectionCommandHandler(_hosts.Object, _collections.Object, _events.Object, _unit.Object);
        await handler.Handle(new CompleteLogCollectionCommand("S1", c.Id, Upload()), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new CompleteLogCollectionCommand("S1", c.Id, [1, 2, 3, 4]), CancellationToken.None));

        Assert.Contains("picked up", ex.Message);
        _events.Verify(e => e.LogsCollectedAsync(_host.Id, It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AHostCannotAnswerAnotherHostsCollection()
    {
        var c = Claimed();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            new CompleteLogCollectionCommandHandler(_hosts.Object, _collections.Object, _events.Object, _unit.Object)
                .Handle(new CompleteLogCollectionCommand("S2", c.Id, Upload()), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            new FailLogCollectionCommandHandler(_hosts.Object, _collections.Object, _events.Object, _unit.Object)
                .Handle(new FailLogCollectionCommand("S2", c.Id, "no"), CancellationToken.None));
        Assert.Equal(LogCollectionStatus.Collecting, c.Status);
    }

    [Fact]
    public async Task AFailureTheAgentReportsIsKeptWithItsReason()
    {
        var c = Claimed();

        await new FailLogCollectionCommandHandler(_hosts.Object, _collections.Object, _events.Object, _unit.Object)
            .Handle(new FailLogCollectionCommand("S1", c.Id, "Reading the log took longer than 5 minutes."), CancellationToken.None);

        Assert.Equal((LogCollectionStatus.Failed, "Reading the log took longer than 5 minutes."), (c.Status, c.FailureReason));
        _events.Verify(e => e.LogsFailedAsync(_host.Id, "Reading the log took longer than 5 minutes.", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeletingIsOnTheTimeline_AndOnlyForTheDevicesOwnCollection()
    {
        var c = Claimed();
        var handler = new DeleteLogCollectionCommandHandler(_collections.Object, _events.Object, _unit.Object);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new DeleteLogCollectionCommand(_other.Id, c.Id, "a"), CancellationToken.None));
        await handler.Handle(new DeleteLogCollectionCommand(_host.Id, c.Id, "juan@htw.com.au"), CancellationToken.None);

        _collections.Verify(r => r.DeleteAsync(c, It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.LogsDeletedAsync(_host.Id, "juan@htw.com.au", "Request cancelled", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ADownloadIsAZipAndIsOnTheTimelineWithTheAdministratorsName()
    {
        var c = Claimed();
        c.Complete(DateTimeOffset.UtcNow, LogPayload.Sanitise(Upload(), 30, DateTimeOffset.UtcNow).Content, 1, null);

        var file = await new GetLogDownloadQueryHandler(_hosts.Object, _collections.Object, _events.Object, _unit.Object)
            .Handle(new GetLogDownloadQuery(_host.Id, c.Id, "juan@htw.com.au"), CancellationToken.None);

        Assert.Equal("pc-logs.zip", file.FileName);
        using var zip = new ZipArchive(new MemoryStream(file.Zip));
        using var reader = new StreamReader(zip.GetEntry("logs.txt")!.Open());
        var text = await reader.ReadToEndAsync();
        Assert.Contains("downloaded by juan@htw.com.au", text);
        Assert.Contains("ERROR\tx\ty", text);
        _events.Verify(e => e.LogsDownloadedAsync(_host.Id, "juan@htw.com.au", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void TheValidatorOnlyAllowsTheTwoPeriodsAndTheTwoDetails()
    {
        var v = new RequestLogCollectionCommandValidator();

        Assert.True(v.Validate(new RequestLogCollectionCommand(_host.Id, 7, "Everything", "a")).IsValid);
        Assert.False(v.Validate(new RequestLogCollectionCommand(_host.Id, 90, "Everything", "a")).IsValid);
        Assert.False(v.Validate(new RequestLogCollectionCommand(_host.Id, 30, "Nonsense", "a")).IsValid);
        // Enum.TryParse alone accepts a number, which would be stored and handed to an agent that cannot read it.
        Assert.False(v.Validate(new RequestLogCollectionCommand(_host.Id, 30, "5", "a")).IsValid);
        Assert.False(v.Validate(new RequestLogCollectionCommand(_host.Id, 30, "-1", "a")).IsValid);
    }
}

public class DeviceReportAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static HealthEventFact Shutdown(double hoursAgo, string source = "Kernel-Power 41", string? detail = null) => new(Now.AddHours(-hoursAgo), HostEventKind.UnexpectedShutdown, source, detail);
    private static HealthEventFact Fatal(double hoursAgo, string source = "WHEA-Logger 18", string? detail = null) => new(Now.AddHours(-hoursAgo), HostEventKind.HardwareErrorFatal, source, detail);
    private static HealthEventFact Corrected(double hoursAgo, string source = "WHEA-Logger 19") => new(Now.AddHours(-hoursAgo), HostEventKind.HardwareErrorCorrected, source, null);
    private static KintsugiActionFact Install(double hoursAgo, string subject = "Zoom") => new(Now.AddHours(-hoursAgo), "Install", subject);

    private static DeviceHypothesisDto Find(IEnumerable<DeviceHypothesisDto> all, string title) => all.Single(h => h.Title.StartsWith(title));

    [Fact]
    public void AFatalHardwareErrorIsTheTopHypothesis_WithItsEvidence()
    {
        var result = DeviceReportAnalyzer.Analyze("Windows", [Shutdown(5), Fatal(5), Fatal(29), Corrected(52)], [], null);

        Assert.Equal("Hardware fault in the processor or memory", result[0].Title);
        Assert.Equal("High", result[0].Confidence);
        Assert.Contains("2 fatal hardware errors", result[0].Evidence[0]);
        Assert.Contains(result[0].CheckNext, n => n.Contains("memory test"));
    }

    [Fact]
    public void ADiskThatSmartSaysIsFailingPointsAtTheDisk_NotTheMemory()
    {
        var result = DeviceReportAnalyzer.Analyze("macOS", [Shutdown(8, "macOS kernel panic report"), Fatal(30, "macOS disk S.M.A.R.T.", "The disk's S.M.A.R.T. status says it is failing")], [], null);

        Assert.Equal("A failing disk or SSD", result[0].Title);
        Assert.Equal("High", result[0].Confidence);
        Assert.DoesNotContain(result, h => h.Title.StartsWith("Hardware fault"));
    }

    [Fact]
    public void StorageWarningsInTheCollectedLog_AreEvidenceOnlyOnceTheLogIsThere()
    {
        var shutdowns = new[] { Shutdown(5) };
        var log = Enumerable.Range(0, 6).Select(i => new LogEntryDto(Now.AddHours(-i), "Warning", "disk 153", "The IO operation at logical block address was retried.")).ToList();

        Assert.DoesNotContain(DeviceReportAnalyzer.Analyze("Windows", shutdowns, [], null), h => h.Title.StartsWith("A failing disk"));
        var withLog = Find(DeviceReportAnalyzer.Analyze("Windows", shutdowns, [], log), "A failing disk");
        Assert.Equal("Medium", withLog.Confidence);
        Assert.Contains("6 storage warnings", withLog.Evidence[0]);
    }

    [Fact]
    public void KintsugiIsUnlikelyWhenNothingWasInstalledJustBefore()
    {
        var h = Find(DeviceReportAnalyzer.Analyze("Windows", [Shutdown(5), Shutdown(29)], [Install(31), Install(6.5)], null), "Kintsugi patching");

        Assert.Equal("Unlikely", h.Confidence);
        Assert.Contains("None of the 2 shutdowns", h.Evidence[^1]);
    }

    [Fact]
    public void KintsugiIsPossiblyInvolvedOnlyWithinThirtyMinutes_AndTheEvidenceSaysHowLong()
    {
        var h = Find(DeviceReportAnalyzer.Analyze("Windows", [Shutdown(5), Shutdown(29), Shutdown(77), Shutdown(150)], [Install(5.2)], null), "Kintsugi patching");

        Assert.Equal("Low", h.Confidence);
        Assert.Contains("Zoom was installed 12 min before", h.Evidence[0]);
        Assert.Contains("1 of 4 shutdowns", h.Evidence[^1]);
    }

    [Fact]
    public void NearestBeforeIgnoresWhatHappenedAfterTheEvent()
    {
        var near = DeviceReportAnalyzer.NearestBefore(Now, [Install(-1), Install(0.25)]);

        Assert.True(near!.Within30Minutes);
        Assert.Equal(15, near.MinutesBefore);
        Assert.Null(DeviceReportAnalyzer.NearestBefore(Now, [Install(-1)]));
    }

    [Fact]
    public void PowerAndBatteryAreNotRuledOutWhereKintsugiCannotSeeThem()
    {
        var windows = Find(DeviceReportAnalyzer.Analyze("Windows", [Shutdown(5)], [], null), "The battery or the power supply");
        var mac = DeviceReportAnalyzer.Analyze("macOS", [Shutdown(5)], [], null);

        Assert.Contains("does not read battery or power state on Windows", windows.Evidence[0]);
        Assert.DoesNotContain(mac, h => h.Title.StartsWith("The battery"));
    }

    [Fact]
    public void ABatteryThatNeedsServiceIsAHypothesis()
    {
        var result = DeviceReportAnalyzer.Analyze("macOS", [new HealthEventFact(Now, HostEventKind.HardwareErrorCorrected, "macOS battery condition", "The battery's condition is \"Service Recommended\"")], [], null);

        Assert.Equal("Medium", Find(result, "The battery").Confidence);
    }

    /// <summary>The battery guess used to sort as Medium while showing Low, so it could sit above a real Medium one.</summary>
    [Fact]
    public void ABatteryGuessSortsByTheConfidenceItShows()
    {
        var shutdowns = new[] { Shutdown(5, detail: "The system lost power (power was lost)"), Shutdown(30, detail: "The system lost power (power was lost)") };
        var result = DeviceReportAnalyzer.Analyze("Windows", shutdowns, [Install(5.1)], null).ToList();

        Assert.Equal("Low", Find(result, "The battery").Confidence);
        var rank = new Dictionary<string, int> { ["High"] = 0, ["Medium"] = 1, ["Low"] = 2 };
        Assert.Equal(result.Select(h => rank[h.Confidence]).OrderBy(r => r), result.Select(h => rank[h.Confidence]));   // never a Low above a Medium
    }

    [Fact]
    public void ADeviceWithNothingLoggedHasNothingToSay() =>
        Assert.Empty(DeviceReportAnalyzer.Analyze("Linux", [], [Install(1)], null));

    [Theory]
    [InlineData("Windows 11 Pro", "Windows")]
    [InlineData("macOS 14.6.1", "macOS")]
    [InlineData("Ubuntu Server 24.04", "Linux")]
    [InlineData(null, "Unknown")]
    public void ThePlatformIsWorkedOutFromTheOperatingSystemName(string? os, string expected) => Assert.Equal(expected, DevicePlatform.Of(os));
}

public class GetDeviceReportQueryHandlerTests
{
    private readonly Mock<IHostRepository> _hosts = new();
    private readonly Mock<IHostEventRepository> _events = new();
    private readonly Mock<ILogCollectionRepository> _collections = new();
    private readonly Host _host = new("LAPTOP-CEO-01", "S1", "Windows 11 Pro");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public GetDeviceReportQueryHandlerTests()
    {
        _hosts.Setup(h => h.GetByIdAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_host);
    }

    private HostEvent E(HostEventKind kind, string subject, double hoursAgo, string? detail = null) =>
        HostEvent.Record(_host.Id, kind, subject, Now.AddHours(-hoursAgo), detail: detail);

    private async Task<DeviceReportDto> Report(int days, params HostEvent[] events)
    {
        _events.Setup(r => r.GetForHostAsync(_host.Id, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(events);
        return await new GetDeviceReportQueryHandler(_hosts.Object, _events.Object, _collections.Object)
            .Handle(new GetDeviceReportQuery(_host.Id, days), CancellationToken.None);
    }

    [Fact]
    public async Task TheReportPutsTheEventsTheInstallsAndTheVerdictTogether()
    {
        var report = await Report(7,
            E(HostEventKind.UnexpectedShutdown, "Kernel-Power 41", 5),
            E(HostEventKind.HardwareErrorFatal, "WHEA-Logger 18", 5.1),
            E(HostEventKind.UnexpectedShutdown, "Kernel-Power 41", 29),
            E(HostEventKind.ApplicationPatched, "Zoom", 5.2),
            E(HostEventKind.PatchFailed, "Adobe Acrobat Reader", 48, "ERROR 1603"),
            E(HostEventKind.UnexpectedShutdown, "Kernel-Power 41", 300));

        Assert.Equal("Windows", report.Host.Platform);
        Assert.Equal("Unstable", report.Health.Status);
        Assert.Equal(3, report.Health.Shutdowns30d);
        Assert.Equal(3, report.Events.Count);                       // the 300-hour-old shutdown is outside 7 days
        Assert.Equal(1, report.ShutdownsNearKintsugi);               // only the one 12 minutes after the Zoom install
        Assert.True(report.Events.First(e => e.Kind == "UnexpectedShutdown").KintsugiNearby is null);   // oldest in the window: nothing before it
        Assert.Equal(["Adobe Acrobat Reader", "Zoom"], report.InstallRuns.Select(r => r.Subject).Reverse());
        Assert.Equal("ERROR 1603", report.InstallRuns.Single(r => !r.Succeeded).Output);
        Assert.Equal("Hardware fault in the processor or memory", report.Hypotheses[0].Title);
        Assert.Null(report.Logs);
    }

    /// <summary>A newer request that is still waiting (or failed) must not hide the last log that did complete.</summary>
    [Fact]
    public async Task ANewRequestThatFailedDoesNotHideTheLastCompletedLog()
    {
        var disk = Enumerable.Range(0, 6).Select(i => JsonSerializer.Serialize(new { t = Now.AddHours(-6 - i), sev = "Warning", src = "disk 153", msg = "The IO operation at logical block address was retried." }));
        using var ms = new MemoryStream();
        using (var zip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            zip.Write(Encoding.UTF8.GetBytes($"{{\"entries\":[{string.Join(",", disk)}],\"truncatedBefore\":null}}"));
        }

        var old = LogCollection.Request(_host.Id, "a", 7, LogCollectionDetail.WarningsAndErrors, Now.AddDays(-2));
        old.MarkClaimed(Now.AddDays(-2));
        var sanitised = LogPayload.Sanitise(ms.ToArray(), 7, Now);
        old.Complete(Now.AddDays(-2).AddMinutes(1), sanitised.Content, sanitised.Count, null);
        var failed = new LogCollectionSummary(Guid.NewGuid(), _host.Id, LogCollectionStatus.Failed, Now.AddHours(-1), "a", 7, LogCollectionDetail.WarningsAndErrors,
            Now.AddHours(23), Now.AddHours(-1), Now, "The device did not send its logs.", 0, 0, null);
        _collections.Setup(r => r.GetLatestSummaryForHostAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(failed);
        _collections.Setup(r => r.GetLatestCompletedForHostAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(old);

        var report = await Report(7, E(HostEventKind.UnexpectedShutdown, "Kernel-Power 41", 5));

        Assert.Equal("Failed", report.Logs!.Status);                                  // the notice is about the newest request
        Assert.Contains(report.Hypotheses, h => h.Title.StartsWith("A failing disk"));   // the older log still feeds the guesses
        Assert.Equal(old.Id, report.LastCompletedLogs!.Id);                           // and the panel can still show it, and delete it
        Assert.Equal("Completed", report.LastCompletedLogs.Status);
    }

    [Fact]
    public async Task ThirtyDaysWidensTheWindowButNotTheVerdict()
    {
        var events = new[] { E(HostEventKind.UnexpectedShutdown, "Kernel-Power 41", 300), E(HostEventKind.UnexpectedShutdown, "Kernel-Power 41", 5) };

        var week = await Report(7, events);
        var month = await Report(30, events);

        Assert.Single(week.Events);
        Assert.Equal(2, month.Events.Count);
        Assert.Equal(week.Health, month.Health);
    }

    [Fact]
    public async Task AnUnknownDeviceIsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            new GetDeviceReportQueryHandler(_hosts.Object, _events.Object, _collections.Object).Handle(new GetDeviceReportQuery(Guid.NewGuid(), 7), CancellationToken.None));
    }
}

public class SearchDevicesQueryHandlerTests
{
    private static Host H(string name, string serial, string os = "Windows 11 Pro") => new(name, serial, os);

    private static SearchDevicesQueryHandler Handler(params Host[] hosts) => Handler(hosts, []);

    private static SearchDevicesQueryHandler Handler(Host[] hosts, HealthEventRecord[] events)
    {
        var repo = new Mock<IHostRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(hosts);
        var eventRepo = new Mock<IHostEventRepository>();
        eventRepo.Setup(r => r.GetHealthEventsSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(events);
        return new SearchDevicesQueryHandler(repo.Object, eventRepo.Object);
    }

    /// <summary>A row says the same thing the Device health panel would: a flagged device is shown flagged, a
    /// device with nothing logged is Healthy, and one device's events never colour another's row.</summary>
    [Fact]
    public async Task EachResultCarriesItsOwnRealHealth()
    {
        var sick = H("LAPTOP-CEO-01", "S1");
        var well = H("LAPTOP-CEO-02", "S2");
        var events = new[]
        {
            new HealthEventRecord(sick.Id, HostEventKind.HardwareErrorFatal, DateTimeOffset.UtcNow.AddHours(-5)),
            new HealthEventRecord(sick.Id, HostEventKind.UnexpectedShutdown, DateTimeOffset.UtcNow.AddHours(-5)),
        };

        var result = await Handler([sick, well], events).Handle(new SearchDevicesQuery("ceo", 10), CancellationToken.None);

        Assert.Equal(("Unstable", true), (result[0].Status, result[0].Why.Contains("fatal hardware error")));
        Assert.Equal(("Healthy", ""), (result[1].Status, result[1].Why));
    }

    [Fact]
    public async Task MatchesByNameOrSerial_IgnoringCase_NamesStartingWithTheTextFirst()
    {
        var result = await Handler(H("sim-win-01", "SIMW-1"), H("LAPTOP-CEO-01", "7XK2M93"), H("old-ceo-laptop", "Z1"), H("mac-01", "F2LX", "macOS 14"))
            .Handle(new SearchDevicesQuery("ceo", 10), CancellationToken.None);

        Assert.Equal(["LAPTOP-CEO-01", "old-ceo-laptop"], result.Select(r => r.Hostname).OrderBy(n => n));
        Assert.Equal("Windows", result[0].Platform);
        Assert.Single(await Handler(H("a", "7XK2M93")).Handle(new SearchDevicesQuery("7xk2", 10), CancellationToken.None));
    }

    [Fact]
    public async Task ABlankSearchReturnsNothing_AndTheListIsCapped()
    {
        var many = Enumerable.Range(0, 50).Select(i => H($"sim-win-{i:00}", $"S{i}")).ToArray();

        Assert.Empty(await Handler(many).Handle(new SearchDevicesQuery("  ", 10), CancellationToken.None));
        Assert.Equal(10, (await Handler(many).Handle(new SearchDevicesQuery("sim", 10), CancellationToken.None)).Count);
        Assert.Equal(SearchDevicesQueryHandler.MaxTake, (await Handler(many).Handle(new SearchDevicesQuery("sim", 500), CancellationToken.None)).Count);
    }
}
