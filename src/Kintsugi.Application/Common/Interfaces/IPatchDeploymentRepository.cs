using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IPatchDeploymentRepository
{
    Task<PatchDeployment?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PatchDeployment>> GetAllAsync(CancellationToken cancellationToken);
    Task AddAsync(PatchDeployment deployment, CancellationToken cancellationToken);
}
