using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.HostActivity;

public class HostEventRecorder : IHostEventRecorder
{
    private readonly IHostEventRepository _repository;

    public HostEventRecorder(IHostEventRepository repository)
    {
        _repository = repository;
    }

    public Task PatchNowRequestedAsync(Guid hostId, string subject, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        Add(HostEvent.Record(hostId, HostEventKind.PatchNowRequested, subject, at, actor: actor), cancellationToken);

    public Task PatchNowPickedUpAsync(Guid hostId, string subject, DateTimeOffset at, CancellationToken cancellationToken) =>
        Add(HostEvent.Record(hostId, HostEventKind.PatchNowPickedUp, subject, at), cancellationToken);

    public Task ApplicationPatchedAsync(Guid hostId, string subject, string? fromVersion, string toVersion, DateTimeOffset at, CancellationToken cancellationToken) =>
        Add(HostEvent.Record(hostId, HostEventKind.ApplicationPatched, subject, at, fromVersion: fromVersion, toVersion: toVersion), cancellationToken);

    public Task OperatingSystemPatchedAsync(Guid hostId, string subject, DateTimeOffset at, CancellationToken cancellationToken) =>
        Add(HostEvent.Record(hostId, HostEventKind.OperatingSystemPatched, subject, at), cancellationToken);

    public async Task PatchFailedAsync(
        Guid hostId, string subject, string? installedVersion, string? attemptedVersion, string details, DateTimeOffset at, CancellationToken cancellationToken)
    {
        // The same failure again is the latest thing that happened to this subject on this host:
        // anything in between (a success, a dismissal) ends the run and the next failure starts
        // a new event.
        var latest = await _repository.GetLatestForSubjectAsync(hostId, subject, cancellationToken);
        if (latest is { Kind: HostEventKind.PatchFailed })
        {
            latest.RecordRepeat(at, details, installedVersion, attemptedVersion);
            return;
        }

        await Add(
            HostEvent.Record(hostId, HostEventKind.PatchFailed, subject, at, fromVersion: installedVersion, toVersion: attemptedVersion, detail: details),
            cancellationToken);
    }

    public Task FailureDismissedAsync(Guid hostId, string subject, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        Add(HostEvent.Record(hostId, HostEventKind.FailureDismissed, subject, at, actor: actor), cancellationToken);

    public Task LogsRequestedAsync(Guid hostId, string actor, string detail, DateTimeOffset at, CancellationToken cancellationToken) =>
        Add(HostEvent.Record(hostId, HostEventKind.LogsRequested, LogsSubject, at, actor: actor, detail: detail), cancellationToken);

    public Task LogsCollectedAsync(Guid hostId, string detail, DateTimeOffset at, CancellationToken cancellationToken) =>
        Add(HostEvent.Record(hostId, HostEventKind.LogsCollected, LogsSubject, at, detail: detail), cancellationToken);

    public Task LogsDownloadedAsync(Guid hostId, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        Add(HostEvent.Record(hostId, HostEventKind.LogsDownloaded, LogsSubject, at, actor: actor), cancellationToken);

    public Task LogsDeletedAsync(Guid hostId, string actor, string what, DateTimeOffset at, CancellationToken cancellationToken) =>
        Add(HostEvent.Record(hostId, HostEventKind.LogsDeleted, LogsSubject, at, actor: actor, detail: what), cancellationToken);

    public Task LogsFailedAsync(Guid hostId, string reason, DateTimeOffset at, CancellationToken cancellationToken) =>
        Add(HostEvent.Record(hostId, HostEventKind.LogsFailed, LogsSubject, at, detail: reason), cancellationToken);

    /// <summary>The subject every log-collection event carries, so they read as one thread on the timeline.</summary>
    public const string LogsSubject = "Device logs";

    private Task Add(HostEvent hostEvent, CancellationToken cancellationToken) =>
        _repository.AddAsync(hostEvent, cancellationToken);
}
