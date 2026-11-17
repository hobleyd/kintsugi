namespace Kintsugi.Domain.Enums;

/// <summary>How much of the operating system's log an administrator asked for.</summary>
public enum LogCollectionDetail
{
    /// <summary>Critical, error and warning entries only. The default: it is what explains a failing
    /// device, and it leaves out most of what could name a person or a file.</summary>
    WarningsAndErrors,

    /// <summary>Everything the agent reads, informational entries included.</summary>
    Everything,
}
