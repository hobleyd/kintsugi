namespace Kintsugi.Application.UpgradePaths;

/// <summary>Live progress of the current (or most recently completed) upgrade-path scan.</summary>
public record UpgradePathScanStatusDto(
    bool IsRunning,
    int Total,
    int Completed,
    int Resolved,
    int NotFound,
    int Failed,
    int Skipped,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FaultReason,
    IReadOnlyList<string> Notes);
