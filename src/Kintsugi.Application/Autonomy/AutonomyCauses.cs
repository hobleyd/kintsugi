using Kintsugi.Application.PatchFailures;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Autonomy;

public enum CauseCategory { Disconnected, Restart, Maintenance, Failure }

/// <summary>A device that is held up by one cause.</summary>
/// <param name="Days">How long it has been held up. The unit the cost of a cause is added up in.</param>
/// <param name="PatchNowEligible">Patch now can act on it: a Windows host held up by a failed Windows
/// update, or one with a Windows update waiting. Never a restart-pending host (Patch now restarts only
/// when its own install says Windows needs one) and never an application failure.</param>
public record CauseDevice(Guid HostId, string Hostname, bool Connected, bool PatchNowEligible, double Days, string Detail);

public record CauseEntry(
    string Key,
    string Title,
    CauseCategory Category,
    bool Measured,
    string? Explanation,
    string? SuggestedFix,
    IReadOnlyList<CauseDevice> Devices)
{
    /// <summary>Device-days of waiting: each device's days held up, added together. It ranks causes by how
    /// much they slow patching down, which the number of devices alone does not.</summary>
    public double DeviceDays => Math.Round(Devices.Sum(d => d.Days), 1);

    public int PatchNowConnected => Devices.Count(d => d.PatchNowEligible && d.Connected);

    public int PatchNowOffline => Devices.Count(d => d.PatchNowEligible && !d.Connected);
}

/// <summary>
/// Works out what is holding patching up and which devices are in each cause. Pure, so the page, the
/// KPI snapshots and Patch now by cause all read the same answer.
/// </summary>
public static class AutonomyCauseAnalyzer
{
    public const string Disconnected = "disconnected";
    public const string Restart = "restart";
    public const string Maintenance = "maintenance";
    public const string FailurePrefix = "failure:";

    public static IReadOnlyList<CauseEntry> Analyze(
        IReadOnlyList<Host> hosts,
        IReadOnlyList<PatchFailureDto> openFailures,
        IReadOnlyDictionary<Guid, int> appUpdates,
        DateTimeOffset now)
    {
        var byId = hosts.ToDictionary(h => h.Id);
        bool Windows(Host h) => h.OperatingSystem?.Contains("Windows", StringComparison.OrdinalIgnoreCase) ?? false;

        var disconnected = hosts
            .Where(h => h.StatusAt(now) == HostStatus.Offline)
            .Select(h => (Host: h, Waiting: (h.OperatingSystemUpdateAvailable == true ? 1 : 0) + appUpdates.GetValueOrDefault(h.Id)))
            .Where(x => x.Waiting > 0)
            .Select(x => new CauseDevice(
                x.Host.Id,
                x.Host.Hostname,
                Connected: false,
                // It will take the request when it checks in, which is what "offline" patchable means.
                PatchNowEligible: Windows(x.Host) && x.Host.OperatingSystemUpdateAvailable == true,
                Days: DaysSince(x.Host.LastSeenUtc, now),
                Detail: $"offline {Ago(x.Host.LastSeenUtc, now) ?? "since it was registered"} · {x.Waiting} update{(x.Waiting == 1 ? "" : "s")} waiting"))
            .OrderByDescending(d => d.Days)
            .ToList();

        var restart = hosts
            .Where(h => h.RestartRequiredSinceUtc is not null)
            .Select(h => new CauseDevice(
                h.Id,
                h.Hostname,
                Connected: h.StatusAt(now) == HostStatus.Online,
                PatchNowEligible: false,
                Days: DaysSince(h.RestartRequiredSinceUtc, now),
                Detail: $"restart pending {Ago(h.RestartRequiredSinceUtc, now)}{(h.RestartRequiredBecause is null ? "" : " · " + h.RestartRequiredBecause)}"))
            .OrderByDescending(d => d.Days)
            .ToList();

        var causes = new List<CauseEntry>
        {
            new(Disconnected, "Disconnected: has not checked in", CauseCategory.Disconnected, true,
                "The device is off, asleep or off the network. Kintsugi cannot reach it, so nothing can be tried. It is not a fault.",
                "Check it is switched on and on a network. A Patch now request is kept for 24 hours and starts when it checks in.",
                disconnected),
            new(Restart, "Updated, restart pending", CauseCategory.Restart, true,
                "The updates are installed but the device has not restarted, usually because the person keeps postponing it.",
                "Ask the person to restart. A shorter delay budget in the Patching Policy forces a postponed restart sooner.",
                restart),
            new(Maintenance, "In maintenance mode", CauseCategory.Maintenance, false,
                "Kintsugi has no maintenance mode yet, so this cannot be measured.",
                null,
                []),
        };

        var failureCauses = openFailures
            .Where(f => byId.ContainsKey(f.HostId))
            .GroupBy(f => f.CauseTitle ?? "An application update failed")
            .Select(g =>
            {
                var devices = g
                    .Select(f => new CauseDevice(
                        f.HostId,
                        f.Hostname,
                        Connected: byId[f.HostId].StatusAt(now) == HostStatus.Online,
                        PatchNowEligible: Windows(byId[f.HostId]) && f.IsOperatingSystemUpdate,
                        Days: DaysSince(f.FirstFailedUtc, now),
                        Detail: $"{(f.IsOperatingSystemUpdate ? "" : f.ApplicationName + " · ")}failed {f.FailureCount} time{(f.FailureCount == 1 ? "" : "s")} · since {f.FirstFailedUtc:yyyy-MM-dd}"))
                    .OrderByDescending(d => d.Days)
                    .ToList();
                return new CauseEntry(
                    FailurePrefix + Slug(g.Key),
                    g.Key,
                    CauseCategory.Failure,
                    true,
                    g.Select(f => f.CauseExplanation).FirstOrDefault(e => e is not null)
                        ?? "The upgrade script for an application errors on these devices.",
                    g.Select(f => f.SuggestedFix).FirstOrDefault(x => x is not null)
                        ?? "Open Failed Updates, read the error and repair the script once: it fixes every device with the same script.",
                    devices);
            })
            .OrderByDescending(c => c.Devices.Count);

        causes.AddRange(failureCauses);
        return causes;
    }

    public static string Slug(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static double DaysSince(DateTimeOffset? when, DateTimeOffset now) =>
        when is null ? 0 : Math.Max(0, Math.Round((now - when.Value).TotalDays, 1));

    /// <summary>"5 days", "9 hours", "12 minutes"; null for a time never reported.</summary>
    public static string? Ago(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is null)
        {
            return null;
        }

        var span = now - when.Value;
        if (span.TotalDays >= 1)
        {
            var days = (int)span.TotalDays;
            return $"{days} day{(days == 1 ? "" : "s")}";
        }

        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            return $"{hours} hour{(hours == 1 ? "" : "s")}";
        }

        var minutes = Math.Max(0, (int)span.TotalMinutes);
        return $"{minutes} minute{(minutes == 1 ? "" : "s")}";
    }
}
