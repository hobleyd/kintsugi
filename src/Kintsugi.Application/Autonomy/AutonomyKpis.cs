using Kintsugi.Application.PatchFailures;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Autonomy;

public enum KpiDirection { LowerIsBetter, HigherIsBetter, Information }

/// <summary>
/// One key figure, defined once. The bands are fixed on purpose: changing a band changes what every past
/// report meant, so they are not editable until there is a way to keep that honest.
/// </summary>
/// <param name="Id">Fixed and stable (for example <c>failures.open_failure_devices</c>): a report or another
/// system can be built on it, and a snapshot is stored under it.</param>
public record KpiDefinition(string Id, string Category, string Definition, KpiDirection Direction, double Green, double Amber, bool IsPercent)
{
    /// <summary>"G", "A", "R", or "I" for a figure that has no band.</summary>
    public string StatusOf(double value) => Direction switch
    {
        KpiDirection.LowerIsBetter => value <= Green ? "G" : value <= Amber ? "A" : "R",
        KpiDirection.HigherIsBetter => value >= Green ? "G" : value >= Amber ? "A" : "R",
        _ => "I",
    };
}

public record KpiValue(KpiDefinition Definition, int Numerator, int Denominator)
{
    public double Value => Definition.IsPercent
        ? (Denominator == 0 ? 0 : Math.Round(Numerator * 100.0 / Denominator, 1))
        : Numerator;
}

public static class AutonomyKpis
{
    public const string AdminStartedShare = "patching.admin_started_share";

    public static readonly IReadOnlyList<KpiDefinition> All =
    [
        new("failures.open_failure_devices", "Failures", "Devices with an open failed update", KpiDirection.LowerIsBetter, 2, 5, true),
        new("patching.os_update_waiting", "Patching", "Devices with an OS update waiting", KpiDirection.LowerIsBetter, 5, 10, true),
        new("patching.app_update_waiting", "Patching", "Devices with an application update waiting", KpiDirection.LowerIsBetter, 15, 25, true),
        new("reachability.unreachable_with_updates", "Reachability", "Devices not seen for over 2 hours with updates waiting", KpiDirection.LowerIsBetter, 5, 10, true),
        new("reachability.checked_in_24h", "Reachability", "Devices that checked in within 24 hours", KpiDirection.HigherIsBetter, 98, 95, true),
        new("restarts.waiting_over_7_days", "Restarts", "Devices waiting for a restart for more than 7 days", KpiDirection.LowerIsBetter, 2, 5, true),
        new("health.unstable_devices", "Device health", "Unstable devices (repeated unexpected shutdowns or a fatal hardware error)", KpiDirection.LowerIsBetter, 0, 3, false),
    ];

    public static readonly KpiDefinition AdminStarted =
        new(AdminStartedShare, "Patching", "Patches started by an administrator (for information)", KpiDirection.Information, 0, 0, true);

    /// <summary>The figures that can be worked out from the fleet as it is now. They are what is
    /// snapshotted every day.</summary>
    public static IReadOnlyList<KpiValue> Compute(
        IReadOnlyList<Host> hosts,
        IReadOnlyList<PatchFailureDto> openFailures,
        IReadOnlyDictionary<Guid, int> appUpdates,
        IReadOnlyList<CauseEntry> causes,
        IReadOnlyList<Kintsugi.Application.DeviceHealth.HostHealthAssessment> health,
        DateTimeOffset now)
    {
        var total = hosts.Count;
        var ids = hosts.Select(h => h.Id).ToHashSet();
        KpiValue V(int index, int numerator) => new(All[index], numerator, total);

        return
        [
            V(0, openFailures.Where(f => ids.Contains(f.HostId)).Select(f => f.HostId).Distinct().Count()),
            V(1, hosts.Count(h => h.OperatingSystemUpdateAvailable == true)),
            V(2, hosts.Count(h => appUpdates.GetValueOrDefault(h.Id) > 0)),
            V(3, causes.First(c => c.Key == AutonomyCauseAnalyzer.Disconnected).Devices.Count),
            V(4, hosts.Count(h => h.LastSeenUtc is not null && h.LastSeenUtc >= now.AddHours(-24))),
            V(5, hosts.Count(h => h.RestartRequiredSinceUtc is not null && h.RestartRequiredSinceUtc <= now.AddDays(-7))),
            V(6, health.Count(a => a.Health == Kintsugi.Application.DeviceHealth.HostHealth.Unstable)),
        ];
    }
}
