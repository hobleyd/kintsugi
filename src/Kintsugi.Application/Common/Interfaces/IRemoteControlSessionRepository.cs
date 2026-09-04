using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IRemoteControlSessionRepository
{
    Task<RemoteControlSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The most recent requests, newest first — the audit view. Capped by the caller
    /// rather than unbounded, since this table only ever grows.</summary>
    Task<IReadOnlyList<RemoteControlSession>> GetRecentAsync(int limit, CancellationToken cancellationToken);

    Task AddAsync(RemoteControlSession session, CancellationToken cancellationToken);
}
