using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.DeviceHealth;

public enum HostHealth { Healthy, Watch, Unstable }

/// <param name="Shutdowns7d">Unexpected shutdowns in the last 7 days.</param>
public record HostHealthAssessment(
    Guid HostId,
    HostHealth Health,
    int Shutdowns7d,
    int Shutdowns30d,
    int FatalErrors30d,
    int CorrectedErrors30d,
    DateTimeOffset? LastEventUtc,
    int Events30d = 0)
{
    /// <summary>The reason in words, for the page and the report.</summary>
    public string Why
    {
        get
        {
            var parts = new List<string>();
            if (Events30d >= HostHealthAssessor.UnusualVolumeIn30Days)
            {
                parts.Add($"{Events30d} health events reported in 30 days, far more than a device normally logs: it is in a crash loop, or something is sending false reports");
            }

            if (Shutdowns30d > 0)
            {
                parts.Add($"{Shutdowns7d} unexpected shutdown{(Shutdowns7d == 1 ? "" : "s")} in the last 7 days, {Shutdowns30d} in 30 days");
            }

            if (FatalErrors30d > 0)
            {
                parts.Add($"{FatalErrors30d} fatal hardware error{(FatalErrors30d == 1 ? "" : "s")} in 30 days");
            }

            if (CorrectedErrors30d > 0)
            {
                parts.Add($"{CorrectedErrors30d} corrected hardware error{(CorrectedErrors30d == 1 ? "" : "s")} in 30 days");
            }

            return string.Join(". ", parts);
        }
    }
}

/// <summary>
/// Decides whether a device is healthy, worth watching, or unstable, from what its operating system
/// logged about shutting down by itself and about hardware errors.
/// </summary>
/// <remarks>
/// <b>Per device and over time, never a fleet count.</b> Across a large fleet a few unexpected shutdowns
/// every day are normal (a flat battery, a power cut); a device that does it three times in a week, or logs
/// one fatal hardware error, is a different thing. The thresholds are fixed:
/// <list type="bullet">
/// <item><b>Unstable</b>: 3 or more unexpected shutdowns in 7 days, or any fatal hardware error in 30 days.</item>
/// <item><b>Watch</b>: 1 or 2 unexpected shutdowns in 30 days, or a corrected hardware error.</item>
/// </list>
/// <b>A flood is reported, not hidden.</b> A device with <see cref="UnusualVolumeIn30Days"/> or more events
/// in 30 days is Unstable whatever the kinds: a real crash loop, or a host (or someone on it) sending
/// false reports, and either way a person should look. The server stops storing more past
/// <c>ReportHostHealthEventsCommandHandler.MaxStoredPer30Days</c>, but the device stays flagged.
/// A restart that Windows, a person or Kintsugi asked for is not an unexpected shutdown, and the agents do
/// not report it, so it never counts against a device.
/// </remarks>
public static class HostHealthAssessor
{
    public const int UnstableShutdownsIn7Days = 3;

    /// <summary>Events in 30 days from which a device is reported as sending an unusual amount.</summary>
    public const int UnusualVolumeIn30Days = 100;

    public static HostHealthAssessment Assess(Guid hostId, IEnumerable<HealthEventRecord> events, DateTimeOffset now)
    {
        var week = now.AddDays(-7);
        var month = now.AddDays(-30);
        var mine = events.Where(e => e.HostId == hostId && e.OccurredUtc >= month && e.OccurredUtc <= now.AddMinutes(5)).ToList();

        var shutdowns30 = mine.Count(e => e.Kind == HostEventKind.UnexpectedShutdown);
        var shutdowns7 = mine.Count(e => e.Kind == HostEventKind.UnexpectedShutdown && e.OccurredUtc >= week);
        var fatal = mine.Count(e => e.Kind == HostEventKind.HardwareErrorFatal);
        var corrected = mine.Count(e => e.Kind == HostEventKind.HardwareErrorCorrected);

        var health = shutdowns7 >= UnstableShutdownsIn7Days || fatal >= 1 || mine.Count >= UnusualVolumeIn30Days
            ? HostHealth.Unstable
            : shutdowns30 >= 1 || corrected >= 1
                ? HostHealth.Watch
                : HostHealth.Healthy;

        return new HostHealthAssessment(
            hostId, health, shutdowns7, shutdowns30, fatal, corrected,
            mine.Count == 0 ? null : mine.Max(e => e.OccurredUtc),
            mine.Count);
    }

    /// <summary>The devices that are not healthy, unstable first, then by how much they have logged.</summary>
    public static IReadOnlyList<HostHealthAssessment> AssessAll(IEnumerable<HealthEventRecord> events, DateTimeOffset now)
    {
        var list = events.ToList();
        return list
            .Select(e => e.HostId)
            .Distinct()
            .Select(id => Assess(id, list, now))
            .Where(a => a.Health != HostHealth.Healthy)
            .OrderByDescending(a => a.Health)
            .ThenByDescending(a => a.Shutdowns30d + a.FatalErrors30d)
            .ThenByDescending(a => a.LastEventUtc)
            .ToList();
    }
}
