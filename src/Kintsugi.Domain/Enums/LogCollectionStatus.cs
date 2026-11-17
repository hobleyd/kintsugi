namespace Kintsugi.Domain.Enums;

public enum LogCollectionStatus
{
    /// <summary>Waiting for the device's agent to look for it at its next check-in.</summary>
    Requested,

    /// <summary>The agent has picked it up and is reading the log.</summary>
    Collecting,

    /// <summary>The log was sent back and is stored.</summary>
    Completed,

    /// <summary>The agent could not read the log, or never answered.</summary>
    Failed,
}
