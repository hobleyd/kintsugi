using Kintsugi.Application.LogCollections;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface ILogCollectionRepository
{
    Task AddAsync(LogCollection collection, CancellationToken cancellationToken);

    /// <summary>One collection, tracked, with its content.</summary>
    Task<LogCollection?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The newest collection for a host still waiting for, or being read by, an agent (by its stored status;
    /// the caller checks <c>IsActiveAt</c>).</summary>
    Task<LogCollection?> GetOpenForHostAsync(Guid hostId, CancellationToken cancellationToken);

    /// <summary>The oldest request an agent can pick up now, tracked.</summary>
    Task<LogCollection?> GetCollectableForHostAsync(Guid hostId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The newest collection for a host, without its content: what the page polls.</summary>
    Task<LogCollectionSummary?> GetLatestSummaryForHostAsync(Guid hostId, CancellationToken cancellationToken);

    /// <summary>The newest completed collection for a host, with its content.</summary>
    Task<LogCollection?> GetLatestCompletedForHostAsync(Guid hostId, CancellationToken cancellationToken);

    Task DeleteAsync(LogCollection collection, CancellationToken cancellationToken);

    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
