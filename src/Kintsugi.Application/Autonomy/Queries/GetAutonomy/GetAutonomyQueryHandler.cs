using System.Globalization;
using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Autonomy.Queries.GetAutonomy;

public class GetAutonomyQueryHandler : IRequestHandler<GetAutonomyQuery, AutonomyDto>
{
    /// <summary>How many months of history the monthly chart reaches back, whatever the retention.</summary>
    public const int MonthsShown = 12;

    public const int EventsRead = 500;
    public const int EventsShown = 30;

    /// <summary>A KPI's "prior" is its value this long ago, from the daily snapshots.</summary>
    public static readonly TimeSpan PriorAge = TimeSpan.FromDays(7);

    /// <summary>How far back the trend lines reach.</summary>
    public static readonly TimeSpan HistoryReach = TimeSpan.FromDays(91);

    private readonly IHostRepository _hosts;
    private readonly IHostEventRepository _events;
    private readonly IPatchFailureRepository _failures;
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly IKpiSnapshotRepository _snapshots;

    public GetAutonomyQueryHandler(
        IHostRepository hosts,
        IHostEventRepository events,
        IPatchFailureRepository failures,
        IUpgradePathRepository upgradePaths,
        IKpiSnapshotRepository snapshots)
    {
        _hosts = hosts;
        _events = events;
        _failures = failures;
        _upgradePaths = upgradePaths;
        _snapshots = snapshots;
    }

    public async Task<AutonomyDto> Handle(GetAutonomyQuery request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to, month) = Window(request, now);

        var inputs = await AutonomyInputs.LoadAsync(_hosts, _failures, _upgradePaths, cancellationToken, _events);
        var names = inputs.Hosts.ToDictionary(h => h.Id, h => h.Hostname);

        // One lean read for the whole chart range, plus a day before it so a pickup just ahead of the first
        // patch is seen. Every period is a slice of this.
        var chartStart = MonthStart(now).AddMonths(-(MonthsShown - 1));
        var activity = await _events.GetPatchActivitySinceAsync(chartStart.AddDays(-1), cancellationToken);
        var patches = AutonomyPatchClassifier.Classify(activity).Where(p => names.ContainsKey(p.HostId)).ToList();
        var inPeriod = patches.Where(p => p.OccurredUtc >= from && p.OccurredUtc <= to).ToList();

        var buckets = new List<AutonomyBucketDto>();
        for (var day = from.UtcDateTime.Date; day <= to.UtcDateTime.Date; day = day.AddDays(1))
        {
            var start = new DateTimeOffset(day, TimeSpan.Zero);
            var dayPatches = inPeriod.Where(p => p.OccurredUtc >= start && p.OccurredUtc < start.AddDays(1)).ToList();
            buckets.Add(new AutonomyBucketDto(start, dayPatches.Count(p => !p.AdminStarted), dayPatches.Count(p => p.AdminStarted)));
        }

        var oldest = await _events.GetOldestEventUtcAsync(cancellationToken);
        var firstMonth = oldest is { } o && MonthStart(o) > chartStart ? MonthStart(o) : chartStart;
        if (oldest is null)
        {
            firstMonth = MonthStart(now);
        }

        var months = new List<AutonomyMonthDto>();
        for (var m = firstMonth; m <= MonthStart(now); m = m.AddMonths(1))
        {
            var monthPatches = patches.Where(p => p.OccurredUtc >= m && p.OccurredUtc < m.AddMonths(1)).ToList();
            months.Add(new AutonomyMonthDto(m.ToString("yyyy-MM", CultureInfo.InvariantCulture), monthPatches.Count(p => !p.AdminStarted), monthPatches.Count(p => p.AdminStarted)));
        }

        var causes = inputs.Causes(now);
        var totalDays = causes.Sum(c => c.DeviceDays);
        var causeDtos = causes
            .Select(c => new AutonomyCauseDto(
                c.Key, c.Title, c.Category.ToString(), c.Measured, c.Devices.Count, c.DeviceDays,
                totalDays <= 0 ? 0 : (int)Math.Round(c.DeviceDays * 100 / totalDays),
                c.Explanation, c.SuggestedFix, c.PatchNowConnected, c.PatchNowOffline))
            .ToList();
        var delayed = causes.SelectMany(c => c.Devices).Select(d => d.HostId).Distinct().Count();

        var snapshots = await _snapshots.GetSinceAsync(now - HistoryReach, cancellationToken);
        var kpis = BuildKpis(inputs, causes, inPeriod.Count, inPeriod.Count(p => p.AdminStarted), snapshots, now);
        var history = snapshots
            .GroupBy(s => s.KpiId)
            .Select(g => new AutonomyKpiHistoryDto(
                g.Key,
                g.OrderBy(s => s.SnapshotDateUtc).Select(s => new AutonomyKpiPointDto(s.SnapshotDateUtc, s.Value)).ToList()))
            .ToList();

        var feed = (await _events.GetEventsSinceAsync(from, EventsRead, cancellationToken))
            .Where(e => e.OccurredUtc <= to && names.ContainsKey(e.HostId))
            .Take(EventsShown)
            .Select(e => new AutonomyEventDto(e.OccurredUtc, e.Kind.ToString(), e.Subject, names[e.HostId], e.Actor, e.Count))
            .ToList();

        return new AutonomyDto(
            request.Period, from, to, month, oldest,
            activity.Select(a => a.HostId).Where(names.ContainsKey).Distinct().Count(),
            inputs.Hosts.Count, delayed, Math.Round(totalDays, 1),
            new AutonomyPatchesDto(inPeriod.Count, inPeriod.Count(p => !p.AdminStarted), inPeriod.Count(p => p.AdminStarted)),
            buckets, months, causeDtos, kpis, history, BuildHealth(inputs), feed);
    }

    private static List<AutonomyKpiDto> BuildKpis(
        AutonomyInputs inputs,
        IReadOnlyList<CauseEntry> causes,
        int patchesInPeriod,
        int adminStarted,
        IReadOnlyList<Kintsugi.Domain.Entities.KpiSnapshot> snapshots,
        DateTimeOffset now)
    {
        var values = AutonomyKpis.Compute(inputs.Hosts, inputs.OpenFailures, inputs.AppUpdates, causes, inputs.Health, now).ToList();
        values.Add(new KpiValue(AutonomyKpis.AdminStarted, adminStarted, patchesInPeriod));

        var priorCutoff = now - PriorAge;
        double? PriorOf(string id) => snapshots
            .Where(s => s.KpiId == id && s.SnapshotDateUtc <= priorCutoff)
            .OrderByDescending(s => s.SnapshotDateUtc)
            .Select(s => (double?)s.Value)
            .FirstOrDefault();

        return values
            .Select(v =>
            {
                var prior = v.Definition.Direction == KpiDirection.Information ? null : PriorOf(v.Definition.Id);
                var d = v.Definition;
                return new AutonomyKpiDto(
                    d.Id, d.Category, d.Definition, d.Direction.ToString(), d.IsPercent, d.Green, d.Amber,
                    v.Value, v.Numerator, v.Denominator, d.StatusOf(v.Value), prior,
                    prior is null ? null : Math.Round(v.Value - prior.Value, 1));
            })
            .ToList();
    }

    private static AutonomyDeviceHealthDto BuildHealth(AutonomyInputs inputs)
    {
        var names = inputs.Hosts.ToDictionary(h => h.Id, h => h.Hostname);
        return new AutonomyDeviceHealthDto(
            inputs.Health.Count(a => a.Health == Kintsugi.Application.DeviceHealth.HostHealth.Unstable),
            inputs.Health.Count(a => a.Health == Kintsugi.Application.DeviceHealth.HostHealth.Watch),
            inputs.Health
                .Take(GetAutonomyQueryHandlerLimits.HealthRows)
                .Select(a => new AutonomyHealthDeviceDto(a.HostId, names[a.HostId], a.Health.ToString(), a.Why, a.LastEventUtc))
                .ToList());
    }

    private static DateTimeOffset MonthStart(DateTimeOffset d) => new(d.Year, d.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private static (DateTimeOffset From, DateTimeOffset To, string? Month) Window(GetAutonomyQuery request, DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        if (request.Period == "7d")
        {
            return (today.AddDays(-6), now, null);
        }

        if (request.Period == "30d")
        {
            return (today.AddDays(-29), now, null);
        }

        var parsed = DateTime.TryParseExact(request.Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m)
            ? new DateTimeOffset(m.Year, m.Month, 1, 0, 0, 0, TimeSpan.Zero)
            : MonthStart(now);
        if (parsed > MonthStart(now))
        {
            parsed = MonthStart(now);
        }

        var end = parsed.AddMonths(1).AddTicks(-1);
        return (parsed, end > now ? now : end, parsed.ToString("yyyy-MM", CultureInfo.InvariantCulture));
    }
}
