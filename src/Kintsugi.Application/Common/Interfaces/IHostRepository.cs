using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IHostRepository
{
    Task<Host?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<Host?> GetBySerialNumberAsync(string serialNumber, CancellationToken cancellationToken);

    /// <summary>Every host that hasn't been removed — see <see cref="Host.DeletedAtUtc"/>. A host
    /// pending removal disappears from this list the moment removal is requested, well before the
    /// agent has actually confirmed it uninstalled itself.</summary>
    Task<IReadOnlyList<Host>> GetAllAsync(CancellationToken cancellationToken);

    Task AddAsync(Host host, CancellationToken cancellationToken);

    /// <summary>Permanently removes the host record — only ever called once the agent has
    /// confirmed it finished uninstalling itself from the host machine (see
    /// ConfirmHostRemovalCommandHandler), at which point there's nothing left to keep even
    /// soft-deleted for.</summary>
    Task DeleteAsync(Host host, CancellationToken cancellationToken);
}
