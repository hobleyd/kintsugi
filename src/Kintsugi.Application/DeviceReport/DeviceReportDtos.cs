using Kintsugi.Application.LogCollections;

namespace Kintsugi.Application.DeviceReport;

public record DeviceReportHostDto(Guid Id, string Hostname, string? OperatingSystem, string Platform, string SerialNumber, string? AgentVersion, DateTimeOffset? LastSeenUtc);

public record DeviceReportHealthDto(string Status, string Why, int Shutdowns7d, int Shutdowns30d, int FatalErrors30d, int CorrectedErrors30d);

/// <summary>The nearest thing Kintsugi did before an event. <c>Within30Minutes</c> is what makes it "possibly involved".</summary>
public record DeviceReportNearbyDto(string Action, string Subject, DateTimeOffset AtUtc, double MinutesBefore, bool Within30Minutes);

/// <param name="Kind">"UnexpectedShutdown", "HardwareErrorFatal" or "HardwareErrorCorrected".</param>
public record DeviceReportEventDto(DateTimeOffset OccurredUtc, string Kind, string Source, string? Detail, DeviceReportNearbyDto? KintsugiNearby);

/// <param name="Kind">"Install" (an application) or "OsUpdate" (a system update, which can restart the device).</param>
public record DeviceReportKintsugiActionDto(DateTimeOffset AtUtc, string Kind, string Subject);

public record DeviceReportInstallRunDto(DateTimeOffset AtUtc, string Subject, bool Succeeded, string? Output);

/// <param name="Confidence">"High", "Medium", "Low" or "Unlikely".</param>
public record DeviceHypothesisDto(string Title, string Confidence, IReadOnlyList<string> Evidence, IReadOnlyList<string> CheckNext);

public record DeviceReportDto(
    DeviceReportHostDto Host,
    int Days,
    DeviceReportHealthDto Health,
    IReadOnlyList<DeviceReportEventDto> Events,
    IReadOnlyList<DeviceReportKintsugiActionDto> KintsugiActions,
    int ShutdownsNearKintsugi,
    IReadOnlyList<DeviceHypothesisDto> Hypotheses,
    IReadOnlyList<DeviceReportInstallRunDto> InstallRuns,
    LogCollectionDto? Logs,
    LogCollectionDto? LastCompletedLogs);
