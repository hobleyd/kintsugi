namespace Kintsugi.Domain.Enums;

/// <summary>
/// What one host's agent found when it checked one baseline item. Crosses to the admin UI as an
/// ordinal, so <c>web/lib/domain/entities/enums.dart</c> mirrors this order: append, never insert.
/// The agent sends the member <em>name</em> instead (see <c>ReportBaselineResultsCommandValidator</c>),
/// so a reordering here cannot silently relabel what a host reported.
/// </summary>
public enum BaselineItemState
{
    /// <summary>The value on the device is the one the signed baseline names.</summary>
    Compliant,

    /// <summary>The value is different or absent. In audit mode that is all that happens: it is reported.</summary>
    Drifted,

    /// <summary>The agent cannot judge this item on this device in this step: a user-scope item (the tray
    /// applies those in a later step) or one whose value is a secret.</summary>
    NotApplicable,

    /// <summary>Reading the value failed (access denied, a read error). Its own state rather than a
    /// drift, so one broken read shows as one failure instead of as a device out of policy.</summary>
    Error
}
