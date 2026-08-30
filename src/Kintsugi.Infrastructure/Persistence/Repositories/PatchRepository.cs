using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class PatchRepository : IPatchRepository
{
    private readonly ApplicationDbContext _context;

    public PatchRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<Patch?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Patches.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Patch>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.Patches.AsNoTracking().ToListAsync(cancellationToken);

    public async Task AddAsync(Patch patch, CancellationToken cancellationToken) =>
        await _context.Patches.AddAsync(patch, cancellationToken);
}
