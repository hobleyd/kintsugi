using Kintsugi.Application.PatchFailures;

namespace Kintsugi.Application.HostActivity;

public record HostEventDto(
    Guid Id,
    DateTimeOffset OccurredUtc,
    string Kind,
    string Subject,
    string? FromVersion,
    string? ToVersion,
    string? Actor,
    string? Detail,
    int Count);

/// <param name="Kind">"Application" or "OperatingSystem".</param>
/// <param name="AvailableVersion">Known for the operating system only: an application's own
/// update check says one is available, not which version.</param>
/// <param name="Failed">An open failure exists for it.</param>
public record PendingUpdateDto(string Name, string Kind, string? CurrentVersion, string? AvailableVersion, bool Failed);

public record HostActivityDto(
    Guid HostId,
    string Hostname,
    string? OperatingSystem,
    string? AgentVersion,
    string Status,
    DateTimeOffset? LastSeenUtc,
    int RetentionMonths,
    IReadOnlyList<HostEventDto> Events,
    IReadOnlyList<PendingUpdateDto> PendingUpdates,
    IReadOnlyList<PatchFailureDto> OpenFailures,
    DateTimeOffset? LastPatchedUtc,
    int PatchedLast30Days,
    DateTimeOffset? RestartRequiredSinceUtc = null,
    string? RestartRequiredBecause = null);

public record HostActivitySettingsDto(int RetentionMonths);
