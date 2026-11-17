using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.DeviceHealth;
using Kintsugi.Application.LogCollections;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.DeviceReport;

/// <param name="Days">7 or 30: the window of the timeline, the events table and the install output. The
/// assessment and the hypotheses always read 30 days, so they do not change when the window does.</param>
public record GetDeviceReportQuery(Guid HostId, int Days) : IRequest<DeviceReportDto>;

public class GetDeviceReportQueryHandler : IRequestHandler<GetDeviceReportQuery, DeviceReportDto>
{
    private static readonly HostEventKind[] HealthKinds = [HostEventKind.UnexpectedShutdown, HostEventKind.HardwareErrorFatal, HostEventKind.HardwareErrorCorrected];

    private readonly IHostRepository _hosts;
    private readonly IHostEventRepository _events;
    private readonly ILogCollectionRepository _collections;

    public GetDeviceReportQueryHandler(IHostRepository hosts, IHostEventRepository events, ILogCollectionRepository collections)
    {
        _hosts = hosts;
        _events = events;
        _collections = collections;
    }

    public async Task<DeviceReportDto> Handle(GetDeviceReportQuery request, CancellationToken cancellationToken)
    {
        var host = await _hosts.GetByIdAsync(request.HostId, cancellationToken)
            ?? throw new NotFoundException($"No device with id '{request.HostId}'.");

        var now = DateTimeOffset.UtcNow;
        var days = request.Days <= 7 ? 7 : 30;
        var month = now.AddDays(-30);
        var period = now.AddDays(-days);
        var platform = DevicePlatform.Of(host.OperatingSystem);

        var all = (await _events.GetForHostAsync(host.Id, 3000, cancellationToken)).Where(e => e.OccurredUtc >= month).ToList();
        var health30 = all.Where(e => HealthKinds.Contains(e.Kind)).ToList();
        var assessment = HostHealthAssessor.Assess(host.Id, health30.Select(e => new HealthEventRecord(host.Id, e.Kind, e.OccurredUtc)).ToList(), now);

        var actions30 = all
            .Where(e => e.Kind is HostEventKind.ApplicationPatched or HostEventKind.OperatingSystemPatched)
            .Select(e => new KintsugiActionFact(e.OccurredUtc, e.Kind == HostEventKind.OperatingSystemPatched ? "OsUpdate" : "Install", e.Subject))
            .OrderBy(a => a.AtUtc)
            .ToList();

        // The newest request may still be waiting or may have failed; the last log that did complete keeps feeding the
        // guesses (and stays in the Logs panel) until a newer one completes or an administrator deletes it.
        var summary = await _collections.GetLatestSummaryForHostAsync(host.Id, cancellationToken);
        var completed = await _collections.GetLatestCompletedForHostAsync(host.Id, cancellationToken);
        IReadOnlyList<LogEntryDto>? log = completed is null ? null : LogPayload.Read(completed.Content);

        var hypotheses = DeviceReportAnalyzer.Analyze(
            platform,
            health30.Select(e => new HealthEventFact(e.OccurredUtc, e.Kind, e.Subject, e.Detail)).ToList(),
            actions30,
            log);

        var events = health30
            .Where(e => e.OccurredUtc >= period)
            .OrderBy(e => e.OccurredUtc)
            .Select(e => new DeviceReportEventDto(
                e.OccurredUtc, e.Kind.ToString(), e.Subject, e.Detail,
                e.Kind == HostEventKind.UnexpectedShutdown ? DeviceReportAnalyzer.NearestBefore(e.OccurredUtc, actions30) : null))
            .ToList();

        var runs = all
            .Where(e => e.OccurredUtc >= period && e.Kind is HostEventKind.ApplicationPatched or HostEventKind.OperatingSystemPatched or HostEventKind.PatchFailed)
            .OrderByDescending(e => e.OccurredUtc)
            .Take(20)
            .Select(e => new DeviceReportInstallRunDto(e.OccurredUtc, e.Subject, e.Kind != HostEventKind.PatchFailed, e.Kind == HostEventKind.PatchFailed ? e.Detail : null))
            .ToList();

        return new DeviceReportDto(
            new DeviceReportHostDto(host.Id, host.Hostname, host.OperatingSystem, platform, host.SerialNumber, host.AgentVersion, host.LastSeenUtc),
            days,
            new DeviceReportHealthDto(assessment.Health.ToString(), assessment.Why, assessment.Shutdowns7d, assessment.Shutdowns30d, assessment.FatalErrors30d, assessment.CorrectedErrors30d),
            events,
            actions30.Where(a => a.AtUtc >= period).Select(a => new DeviceReportKintsugiActionDto(a.AtUtc, a.Kind, a.Subject)).ToList(),
            events.Count(e => e.KintsugiNearby is { Within30Minutes: true }),
            hypotheses,
            runs,
            summary?.ToDto(now),
            completed is null ? null : LogCollectionSummary.From(completed).ToDto(now));
    }
}

public static class DevicePlatform
{
    public static string Of(string? operatingSystem)
    {
        var os = (operatingSystem ?? string.Empty).ToLowerInvariant();
        return os.Contains("windows") ? "Windows"
            : os.Contains("mac") || os.Contains("darwin") ? "macOS"
            : os.Length == 0 ? "Unknown"
            : "Linux";
    }
}
