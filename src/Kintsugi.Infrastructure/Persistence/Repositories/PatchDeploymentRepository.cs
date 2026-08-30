using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class PatchDeploymentRepository : IPatchDeploymentRepository
{
    private readonly ApplicationDbContext _context;

    public PatchDeploymentRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<PatchDeployment?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.PatchDeployments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PatchDeployment>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.PatchDeployments.AsNoTracking().ToListAsync(cancellationToken);

    public async Task AddAsync(PatchDeployment deployment, CancellationToken cancellationToken) =>
        await _context.PatchDeployments.AddAsync(deployment, cancellationToken);
}
