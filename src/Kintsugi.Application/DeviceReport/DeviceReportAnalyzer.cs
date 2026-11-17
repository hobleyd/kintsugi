using System.Text.RegularExpressions;
using Kintsugi.Application.LogCollections;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.DeviceReport;

public record HealthEventFact(DateTimeOffset OccurredUtc, HostEventKind Kind, string Source, string? Detail);

public record KintsugiActionFact(DateTimeOffset AtUtc, string Kind, string Subject);

/// <summary>
/// Reads what one device logged and what Kintsugi did to it, and lists what the problem could be, with the
/// evidence for each and what to check next.
/// </summary>
/// <remarks>
/// <b>A hint, never a verdict.</b> The rules are fixed and every hypothesis carries the evidence it came from. Some
/// evidence (a disk retrying reads) is only in the log, so it appears once a log has been collected. Kintsugi does
/// not read battery or power state on Windows and Linux, and says so rather than ruling it out.
/// </remarks>
public static class DeviceReportAnalyzer
{
    /// <summary>Kintsugi counts as possibly involved only if it installed something in the 30 minutes before.</summary>
    public static readonly TimeSpan InvolvementWindow = TimeSpan.FromMinutes(30);

    private static readonly Regex StorageLine = new(@"\b(disk|nvme|smart|i/o error|ata\d|blk_update_request|scsi|ntfs|filesystem|file system)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StorageEvent = new("s\\.m\\.a\\.r\\.t|nvme|\\bssd\\b|\\bdisk\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DeviceReportNearbyDto? NearestBefore(DateTimeOffset at, IReadOnlyList<KintsugiActionFact> actions)
    {
        var nearest = actions.Where(a => a.AtUtc <= at).OrderByDescending(a => a.AtUtc).FirstOrDefault();
        if (nearest is null)
        {
            return null;
        }

        var before = at - nearest.AtUtc;
        return new DeviceReportNearbyDto(nearest.Kind, nearest.Subject, nearest.AtUtc, Math.Round(before.TotalMinutes, 1), before <= InvolvementWindow);
    }

    public static IReadOnlyList<DeviceHypothesisDto> Analyze(
        string platform,
        IReadOnlyList<HealthEventFact> health,
        IReadOnlyList<KintsugiActionFact> actions,
        IReadOnlyList<LogEntryDto>? log)
    {
        var shutdowns = health.Where(e => e.Kind == HostEventKind.UnexpectedShutdown).ToList();
        var fatal = health.Where(e => e.Kind == HostEventKind.HardwareErrorFatal).ToList();
        var corrected = health.Where(e => e.Kind == HostEventKind.HardwareErrorCorrected).ToList();

        var storageEvents = health.Where(e => e.Kind != HostEventKind.UnexpectedShutdown && StorageEvent.IsMatch($"{e.Source} {e.Detail}")).ToList();
        var memoryOrCpuFatal = fatal.Except(storageEvents).ToList();
        var memoryOrCpuCorrected = corrected.Except(storageEvents).ToList();
        var batteryEvents = health.Where(e => e.Source.Contains("battery", StringComparison.OrdinalIgnoreCase)).ToList();
        var storageLog = (log ?? []).Where(e => e.Sev is "Error" or "Warning" or "Critical" && StorageLine.IsMatch($"{e.Src} {e.Msg}")).ToList();
        var powerShutdowns = shutdowns.Where(e => Regex.IsMatch(e.Detail ?? "", "power (was lost|button)", RegexOptions.IgnoreCase)).ToList();
        var panics = shutdowns.Where(e => e.Source.Contains("panic", StringComparison.OrdinalIgnoreCase)).ToList();

        var result = new List<(int rank, int evidence, DeviceHypothesisDto h)>();

        if (memoryOrCpuFatal.Count > 0 || memoryOrCpuCorrected.Count >= 3 || (memoryOrCpuCorrected.Count > 0 && memoryOrCpuFatal.Count == 0 && shutdowns.Count > 0))
        {
            var evidence = new List<string>();
            if (memoryOrCpuFatal.Count > 0)
            {
                evidence.Add($"{memoryOrCpuFatal.Count} fatal hardware error{S(memoryOrCpuFatal.Count)} in 30 days ({Sources(memoryOrCpuFatal)}).");
                if (shutdowns.Count > 0)
                {
                    evidence.Add("A fatal hardware error is the processor or memory reporting it cannot continue, which looks like a sudden shutdown, not a software crash.");
                }
            }

            if (memoryOrCpuCorrected.Count > 0)
            {
                evidence.Add($"{memoryOrCpuCorrected.Count} corrected hardware error{S(memoryOrCpuCorrected.Count)} as well, which often come before the fatal ones.");
            }

            var confidence = memoryOrCpuFatal.Count > 0 ? "High" : memoryOrCpuCorrected.Count >= 3 ? "Medium" : "Low";
            result.Add((Rank(confidence), evidence.Count, new DeviceHypothesisDto("Hardware fault in the processor or memory", confidence, evidence, MemoryNext(platform))));
        }

        if (storageEvents.Count > 0 || storageLog.Count >= 2)
        {
            var evidence = new List<string>();
            foreach (var e in storageEvents.Take(3))
            {
                evidence.Add(e.Detail ?? e.Source);
            }

            if (storageLog.Count >= 2)
            {
                evidence.Add($"{storageLog.Count} storage warnings or errors in the collected log (for example: {Trim(storageLog.OrderByDescending(e => e.T).First().Msg, 110)}).");
            }

            var confidence = storageEvents.Any(e => e.Kind == HostEventKind.HardwareErrorFatal) ? "High" : storageLog.Count >= 5 || storageEvents.Count > 0 ? "Medium" : "Low";
            result.Add((Rank(confidence), evidence.Count, new DeviceHypothesisDto("A failing disk or SSD", confidence, evidence,
                ["Back up the device before any other step.", platform == "macOS" ? "Book a repair at an authorised service provider: the SSD is part of the board on current Macs." : "Check the disk's health in the maker's tool or with SMART, and replace it if it reports problems."])));
        }

        if (batteryEvents.Count > 0 || powerShutdowns.Count >= 2)
        {
            var evidence = new List<string>();
            if (batteryEvents.Count > 0)
            {
                evidence.Add(batteryEvents[0].Detail ?? "The battery reports it needs service.");
            }

            if (powerShutdowns.Count > 0)
            {
                evidence.Add($"{powerShutdowns.Count} shutdown{S(powerShutdowns.Count)} blamed on power being lost or the power button held down.");
            }

            var batteryConfidence = batteryEvents.Count > 0 ? "Medium" : "Low";
            result.Add((Rank(batteryConfidence), evidence.Count, new DeviceHypothesisDto("The battery or the power supply", batteryConfidence, evidence,
                ["Replace the battery at the next service.", "Ask whether it shut down on battery or on the charger."])));
        }
        else if (shutdowns.Count > 0 && platform is "Windows" or "Linux")
        {
            result.Add((Rank("Low"), 0, new DeviceHypothesisDto("The battery or the power supply", "Low",
                [$"Kintsugi does not read battery or power state on {platform}, so there is nothing here to confirm or rule it out."],
                ["Ask whether it shut down on battery or on the charger, and check the power supply."])));
        }

        if (shutdowns.Count > 0 && fatal.Count == 0 && storageEvents.Count == 0)
        {
            var evidence = new List<string> { $"{shutdowns.Count} unexpected shutdown{S(shutdowns.Count)} with no hardware error logged." };
            if (panics.Count > 0)
            {
                evidence.Add($"{panics.Count} kernel panic record{S(panics.Count)} saved, which points at the operating system or a driver.");
            }

            result.Add((Rank(panics.Count > 0 ? "Medium" : "Low"), evidence.Count, new DeviceHypothesisDto("A software or driver crash", panics.Count > 0 ? "Medium" : "Low", evidence,
                ["Update the firmware, drivers and operating system.", log is null ? "Collect the log to read what ran before each shutdown." : "Read the log lines around each shutdown."])));
        }

        var involved = shutdowns.Select(s => (s, near: NearestBefore(s.OccurredUtc, actions))).Where(x => x.near is { Within30Minutes: true }).ToList();
        if (shutdowns.Count > 0)
        {
            var evidence = new List<string>();
            foreach (var (s, near) in involved.Take(3))
            {
                evidence.Add($"{near!.Subject} was installed {Minutes(near.MinutesBefore)} before the shutdown on {s.OccurredUtc:d MMM HH:mm} UTC.");
            }

            evidence.Add(involved.Count == 0
                ? $"None of the {shutdowns.Count} shutdown{S(shutdowns.Count)} came within 30 minutes of an install by Kintsugi."
                : $"{involved.Count} of {shutdowns.Count} shutdown{S(shutdowns.Count)} came within 30 minutes of an install; the others did not.");
            var confidence = involved.Count == 0 ? "Unlikely" : involved.Count * 3 > shutdowns.Count ? "Medium" : "Low";
            result.Add((Rank(confidence), involved.Count, new DeviceHypothesisDto("Kintsugi patching", confidence, evidence,
                involved.Count == 0 ? ["Nothing to do on the patching side."] : ["Check whether the installer left the device busy, and whether a restart was asked for."])));
        }

        return result
            .OrderBy(r => r.rank).ThenByDescending(r => r.evidence)
            .Select(r => r.h)
            .ToList();
    }

    private static int Rank(string confidence) => confidence switch { "High" => 0, "Medium" => 1, "Low" => 2, _ => 3 };

    private static string S(int n) => n == 1 ? "" : "s";

    private static string Minutes(double m) => m < 60 ? $"{Math.Round(m)} min" : $"{(int)(m / 60)} h {Math.Round(m % 60):00} min";

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Sources(IEnumerable<HealthEventFact> events) => string.Join(", ", events.Select(e => e.Source).Distinct().Take(3));

    private static IReadOnlyList<string> MemoryNext(string platform) => platform switch
    {
        "Windows" => ["Run the maker's diagnostics and a memory test.", "Update the BIOS and chipset drivers.", "If a test fails, replace the device: it will keep losing work until then."],
        "macOS" => ["Run Apple Diagnostics (hold D while starting up).", "Book a repair if it reports a fault."],
        _ => ["Replace or reseat the memory module named in the log.", "Run a memory test in a maintenance window.", "Check the server's management controller (iDRAC, iLO) hardware log."],
    };
}
