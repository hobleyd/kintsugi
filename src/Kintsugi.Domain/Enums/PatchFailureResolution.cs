namespace Kintsugi.Domain.Enums;

/// <summary>
/// Whether a reported patch failure is still outstanding, and if not, what settled it. The Failed
/// Updates screen filters on this: <see cref="Outstanding"/> is what it shows by default, since a
/// row that has since patched successfully is history rather than a problem.
/// </summary>
/// <remarks>
/// No JSON converter, like <see cref="HostStatus"/> and <see cref="AiProvider"/>, so this crosses
/// the wire as an ordinal — which makes declaration order load-bearing on both ends. Append new
/// members; never insert or reorder. The mirror is <c>PatchFailureResolution</c> in
/// <c>web/lib/domain/entities/enums.dart</c>.
/// </remarks>
public enum PatchFailureResolution
{
    /// <summary>Still failing, as far as anything has told this server.</summary>
    Outstanding = 0,

    /// <summary>The same host later reported patching this application successfully — see
    /// <c>ReportPatchResultCommandHandler</c>, which closes outstanding failures as a side effect
    /// of the success report rather than leaving the screen to accumulate rows nobody can act on.</summary>
    PatchSucceeded = 1,

    /// <summary>An administrator dismissed it from the Failed Updates screen.</summary>
    Dismissed = 2
}
