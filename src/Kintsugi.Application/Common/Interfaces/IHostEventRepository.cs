using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IHostEventRepository
{
    Task AddAsync(HostEvent hostEvent, CancellationToken cancellationToken);

    /// <summary>The most recent event for one host and subject, or null. Used to fold a repeated
    /// failure into the event already on the timeline.</summary>
    Task<HostEvent?> GetLatestForSubjectAsync(Guid hostId, string subject, CancellationToken cancellationToken);

    /// <summary>A host's events, newest first.</summary>
    Task<IReadOnlyList<HostEvent>> GetForHostAsync(Guid hostId, int take, CancellationToken cancellationToken);

    /// <summary>The patches and Patch now pickups since <paramref name="sinceUtc"/>, without their
    /// output, for the Autonomy page.</summary>
    Task<IReadOnlyList<Kintsugi.Application.Autonomy.PatchActivityRecord>> GetPatchActivitySinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>Every host's events since <paramref name="sinceUtc"/>, newest first, at most
    /// <paramref name="take"/>.</summary>
    Task<IReadOnlyList<HostEvent>> GetEventsSinceAsync(DateTimeOffset sinceUtc, int take, CancellationToken cancellationToken);

    /// <summary>Every host's unexpected shutdowns and hardware errors since <paramref name="sinceUtc"/>, for
    /// the health assessment.</summary>
    Task<IReadOnlyList<Kintsugi.Application.DeviceHealth.HealthEventRecord>> GetHealthEventsSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>One host's health events since <paramref name="sinceUtc"/>, to tell a report of an event it
    /// already has from a new one.</summary>
    Task<IReadOnlyList<Kintsugi.Application.DeviceHealth.HealthEventRecord>> GetHealthEventsForHostSinceAsync(Guid hostId, DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>When the oldest event still kept happened, or null when there are none.</summary>
    Task<DateTimeOffset?> GetOldestEventUtcAsync(CancellationToken cancellationToken);

    /// <summary>Removes every event older than <paramref name="cutoffUtc"/>; returns how many.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
