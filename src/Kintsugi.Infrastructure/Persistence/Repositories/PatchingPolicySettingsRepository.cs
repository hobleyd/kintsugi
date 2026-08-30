using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class PatchingPolicySettingsRepository : IPatchingPolicySettingsRepository
{
    private readonly ApplicationDbContext _context;

    public PatchingPolicySettingsRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<PatchingPolicySettings?> GetAsync(CancellationToken cancellationToken) =>
        _context.PatchingPolicySettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(PatchingPolicySettings settings, CancellationToken cancellationToken) =>
        await _context.PatchingPolicySettings.AddAsync(settings, cancellationToken);
}
