namespace Kintsugi.Domain.Enums;

/// <summary>What happened, on the host's activity timeline.</summary>
/// <remarks>
/// Stored by name, so a value can be added but never renamed. Everything here is something the
/// *server* saw; what the person at the host did (the warning shown, a Delay click) is not on this
/// list yet because no agent reports it.
/// </remarks>
public enum HostEventKind
{
    /// <summary>An administrator pressed Patch now. <c>Actor</c> is who.</summary>
    PatchNowRequested,

    /// <summary>The host's agent collected a Patch now request.</summary>
    PatchNowPickedUp,

    /// <summary>An application was patched to a new version.</summary>
    ApplicationPatched,

    /// <summary>The operating system's pending update was installed.</summary>
    OperatingSystemPatched,

    /// <summary>An install failed. Repeats of the same failure are folded into one event with a count.</summary>
    PatchFailed,

    /// <summary>An administrator cleared a failure by hand. <c>Actor</c> is who.</summary>
    FailureDismissed,

    /// <summary>The device shut down without being asked to and without a clean shutdown: power loss, a
    /// crash, a reset. Reported by the agent from the operating system's own log, so a restart that
    /// Windows, a person or Kintsugi asked for is not one.</summary>
    UnexpectedShutdown,

    /// <summary>The processor, memory or platform reported a hardware error it could not correct.</summary>
    HardwareErrorFatal,

    /// <summary>A hardware error the hardware corrected itself. One is not alarming; a run of them is a
    /// warning.</summary>
    HardwareErrorCorrected,

    /// <summary>An administrator asked this device's agent to collect its operating system log.</summary>
    LogsRequested,

    /// <summary>The agent sent the log back.</summary>
    LogsCollected,

    /// <summary>An administrator downloaded a collected log.</summary>
    LogsDownloaded,

    /// <summary>An administrator deleted a collected log, or cancelled a request that had not been picked up.</summary>
    LogsDeleted,

    /// <summary>The agent could not read the log, or never sent it back.</summary>
    LogsFailed,
}
