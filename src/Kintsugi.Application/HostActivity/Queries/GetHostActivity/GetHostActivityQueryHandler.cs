using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.HostActivity.Queries.GetHostActivity;

public class GetHostActivityQueryHandler : IRequestHandler<GetHostActivityQuery, HostActivityDto>
{
    /// <summary>The timeline shows the newest this many; retention keeps the rest in the table.</summary>
    public const int MaxEvents = 500;

    private readonly IHostRepository _hosts;
    private readonly IHostEventRepository _events;
    private readonly IHostActivitySettingsRepository _settings;
    private readonly IInstalledApplicationRepository _applications;
    private readonly IPatchFailureRepository _failures;

    public GetHostActivityQueryHandler(
        IHostRepository hosts,
        IHostEventRepository events,
        IHostActivitySettingsRepository settings,
        IInstalledApplicationRepository applications,
        IPatchFailureRepository failures)
    {
        _hosts = hosts;
        _events = events;
        _settings = settings;
        _applications = applications;
        _failures = failures;
    }

    public async Task<HostActivityDto> Handle(GetHostActivityQuery request, CancellationToken cancellationToken)
    {
        var host = await _hosts.GetByIdAsync(request.HostId, cancellationToken);
        if (host is null || host.DeletedAtUtc is not null)
        {
            throw new NotFoundException($"No host was found with id '{request.HostId}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var events = await _events.GetForHostAsync(host.Id, MaxEvents, cancellationToken);
        var retention = (await _settings.GetAsync(cancellationToken))?.RetentionMonths ?? HostActivitySettings.DefaultRetentionMonths;

        var openFailures = (await _failures.GetAllAsync(cancellationToken))
            .Where(f => f.HostId == host.Id && f.Resolution == PatchFailureResolution.Outstanding)
            .OrderByDescending(f => f.LastFailedUtc)
            .ToList();
        var failingNames = openFailures.Select(f => f.ApplicationName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pending = new List<PendingUpdateDto>();
        if (host.OperatingSystemUpdateAvailable == true)
        {
            var osName = OperatingSystemName(host.OperatingSystem);
            pending.Add(new PendingUpdateDto(osName, "OperatingSystem", host.OperatingSystem, host.OperatingSystemLatestVersion, failingNames.Contains(osName)));
        }

        var apps = await _applications.GetByHostIdAsync(host.Id, cancellationToken);
        pending.AddRange(apps
            .Where(a => a.UpdateAvailable == true)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new PendingUpdateDto(a.Name, "Application", a.Version, null, failingNames.Contains(a.Name))));

        var patched = events
            .Where(e => e.Kind is HostEventKind.ApplicationPatched or HostEventKind.OperatingSystemPatched)
            .ToList();

        return new HostActivityDto(
            host.Id,
            host.Hostname,
            host.OperatingSystem,
            host.AgentVersion,
            host.StatusAt(now).ToString(),
            host.LastSeenUtc,
            retention,
            events.Select(e => new HostEventDto(e.Id, e.OccurredUtc, e.Kind.ToString(), e.Subject, e.FromVersion, e.ToVersion, e.Actor, e.Detail, e.Count)).ToList(),
            pending,
            openFailures,
            patched.Count == 0 ? null : patched.Max(e => e.OccurredUtc),
            patched.Count(e => e.OccurredUtc >= now.AddDays(-30)),
            host.RestartRequiredSinceUtc,
            host.RestartRequiredBecause);
    }

    /// <summary>The name the failure queue uses for the operating system, so a pending OS update
    /// can be matched to its open failure.</summary>
    public static string OperatingSystemName(string? operatingSystem)
    {
        var os = operatingSystem ?? string.Empty;
        if (os.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows";
        }

        if (os.Contains("mac", StringComparison.OrdinalIgnoreCase) || os.Contains("darwin", StringComparison.OrdinalIgnoreCase))
        {
            return "macOS";
        }

        return os.Length == 0 ? "Operating system" : "Linux";
    }
}
