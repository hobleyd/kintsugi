namespace Kintsugi.Application.Autonomy;

public record AutonomyPatchesDto(int Total, int OwnSchedule, int AdminStarted);

/// <param name="StartUtc">The start of the day this bar covers (UTC).</param>
public record AutonomyBucketDto(DateTimeOffset StartUtc, int Own, int Admin);

/// <param name="Month">"2026-11".</param>
public record AutonomyMonthDto(string Month, int Own, int Admin);

/// <summary>
/// One thing holding patching up. Counts and cost only: the devices in it are read a page at a time
/// (<c>GetAutonomyCauseDevicesQuery</c>), so a fleet of any size costs the same to show.
/// </summary>
/// <param name="Key">Fixed ("disconnected", "restart", "maintenance", or "failure:" plus the cause).</param>
/// <param name="Measured">False for a kind Kintsugi cannot see yet, so an empty row is not read as good news.</param>
/// <param name="DeviceDays">Days of waiting, added over every device in it. How much it slows patching.</param>
/// <param name="SharePercent">Its share of all the device-days.</param>
/// <param name="PatchNowConnected">Windows devices in it that Patch now can start right now.</param>
/// <param name="PatchNowOffline">Windows devices in it that would take the request when they check in.</param>
public record AutonomyCauseDto(
    string Key,
    string Title,
    string Category,
    bool Measured,
    int Devices,
    double DeviceDays,
    int SharePercent,
    string? Explanation,
    string? SuggestedFix,
    int PatchNowConnected,
    int PatchNowOffline);

public record AutonomyKpiDto(
    string Id,
    string Category,
    string Definition,
    string Direction,
    bool IsPercent,
    double Green,
    double Amber,
    double Value,
    int Numerator,
    int Denominator,
    string Status,
    double? Prior,
    double? Delta);

public record AutonomyKpiPointDto(DateTimeOffset DateUtc, double Value);

public record AutonomyKpiHistoryDto(string Id, IReadOnlyList<AutonomyKpiPointDto> Points);

public record AutonomyEventDto(
    DateTimeOffset OccurredUtc,
    string Kind,
    string Subject,
    string Hostname,
    string? Actor,
    int Count);

public record AutonomyDeviceDto(Guid HostId, string Hostname, string Detail, bool Connected, bool PatchNowEligible);

public record AutonomyDevicesPageDto(int Total, IReadOnlyList<AutonomyDeviceDto> Items);

/// <param name="Period">"7d", "30d" or "month".</param>
/// <param name="Month">The month shown, for the monthly period.</param>
/// <param name="HistoryStartsUtc">When the first recorded event happened. Nothing before it can be
/// rebuilt, so the page says where the history begins.</param>
/// <param name="DevicesDelayed">Devices in at least one cause.</param>
/// <param name="TotalDeviceDays">Device-days across every cause (a device in two causes counts in both).</param>
public record AutonomyDto(
    string Period,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? Month,
    DateTimeOffset? HistoryStartsUtc,
    int HostsWithEvents,
    int TotalHosts,
    int DevicesDelayed,
    double TotalDeviceDays,
    AutonomyPatchesDto Patches,
    IReadOnlyList<AutonomyBucketDto> Buckets,
    IReadOnlyList<AutonomyMonthDto> Months,
    IReadOnlyList<AutonomyCauseDto> Causes,
    IReadOnlyList<AutonomyKpiDto> Kpis,
    IReadOnlyList<AutonomyKpiHistoryDto> KpiHistory,
    AutonomyDeviceHealthDto DeviceHealth,
    IReadOnlyList<AutonomyEventDto> Events);

/// <param name="Status">"Unstable" or "Watch". A healthy device is not listed.</param>
/// <param name="Why">The reason in words: the counts that put it there.</param>
public record AutonomyHealthDeviceDto(Guid HostId, string Hostname, string Status, string Why, DateTimeOffset? LastEventUtc);

/// <summary>
/// Devices that shut down by themselves or logged hardware errors, read from each operating system's own
/// log. A device per row with its reason, not a group, and capped: the counts are the whole fleet's, the
/// rows are the worst <see cref="GetAutonomyQueryHandlerLimits.HealthRows"/>.
/// </summary>
public record AutonomyDeviceHealthDto(int Unstable, int Watch, IReadOnlyList<AutonomyHealthDeviceDto> Items);

public record AutonomyPatchNowResult(int Started, int QueuedOffline, IReadOnlyList<string> NotRequested);

/// <summary>Limits on what the Autonomy page returns, so its size does not grow with the fleet.</summary>
public static class GetAutonomyQueryHandlerLimits
{
    public const int HealthRows = 25;
}
