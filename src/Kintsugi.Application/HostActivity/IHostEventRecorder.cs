namespace Kintsugi.Application.HostActivity;

/// <summary>
/// Writes to a host's activity timeline. Each method adds to the unit of work and leaves the
/// save to the calling handler, so an event is stored in the same transaction as the change it
/// describes — never one without the other.
/// </summary>
public interface IHostEventRecorder
{
    Task PatchNowRequestedAsync(Guid hostId, string subject, string actor, DateTimeOffset at, CancellationToken cancellationToken);

    Task PatchNowPickedUpAsync(Guid hostId, string subject, DateTimeOffset at, CancellationToken cancellationToken);

    Task ApplicationPatchedAsync(Guid hostId, string subject, string? fromVersion, string toVersion, DateTimeOffset at, CancellationToken cancellationToken);

    Task OperatingSystemPatchedAsync(Guid hostId, string subject, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Folds into the latest event when that is the same failure still going on.</summary>
    Task PatchFailedAsync(Guid hostId, string subject, string? installedVersion, string? attemptedVersion, string details, DateTimeOffset at, CancellationToken cancellationToken);

    Task FailureDismissedAsync(Guid hostId, string subject, string actor, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>An administrator asked for the device's log.</summary>
    Task LogsRequestedAsync(Guid hostId, string actor, string detail, DateTimeOffset at, CancellationToken cancellationToken);

    Task LogsCollectedAsync(Guid hostId, string detail, DateTimeOffset at, CancellationToken cancellationToken);

    Task LogsDownloadedAsync(Guid hostId, string actor, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>A collected log was deleted, or a request not yet picked up was cancelled.</summary>
    Task LogsDeletedAsync(Guid hostId, string actor, string what, DateTimeOffset at, CancellationToken cancellationToken);

    Task LogsFailedAsync(Guid hostId, string reason, DateTimeOffset at, CancellationToken cancellationToken);
}
