namespace Kintsugi.Application.Vanta;

/// <summary>
/// What the background Vanta sync is doing, and how the last run went. Polled by the settings
/// screen, the same way the three upgrade-path coordinators' status routes are.
/// </summary>
/// <remarks>
/// In-memory and therefore lost on restart, like every other coordinator here. That is acceptable
/// precisely because the sync is a state-of-the-world replacement with nothing accumulating between
/// runs: the background service runs one shortly after startup, so "unknown" is only ever the answer
/// for the first minute of a process's life.
/// </remarks>
public record VantaSyncStatusDto(
    bool Running,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    bool? LastRunSucceeded,
    int ComponentCount,
    int PackageCount,
    string? Message);
