namespace Kintsugi.Application.UpgradePaths;

/// <summary>Live progress of the current (or most recently completed) "Check for Updates" run —
/// re-running each existing AI-generated script's own <c>--update-version</c> mode against every
/// resolved script upgrade path, with no AI call involved.</summary>
public record UpdateCheckStatusDto(
    bool IsRunning,
    int Total,
    int Completed,
    int Updated,
    int Unchanged,
    int Failed,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FaultReason);
