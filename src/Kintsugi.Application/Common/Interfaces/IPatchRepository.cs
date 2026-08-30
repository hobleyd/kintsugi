using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IPatchRepository
{
    Task<Patch?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Patch>> GetAllAsync(CancellationToken cancellationToken);
    Task AddAsync(Patch patch, CancellationToken cancellationToken);
}
