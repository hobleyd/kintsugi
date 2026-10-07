using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IAiConnectionRepository
{
    Task<IReadOnlyList<AiConnection>> GetAllAsync(CancellationToken cancellationToken);
    Task<AiConnection?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(AiConnection connection, CancellationToken cancellationToken);
    void Remove(AiConnection connection);
}
