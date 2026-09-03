using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class VantaSettingsRepository : IVantaSettingsRepository
{
    private readonly ApplicationDbContext _context;

    public VantaSettingsRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<VantaSettings?> GetAsync(CancellationToken cancellationToken) =>
        _context.VantaSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(VantaSettings settings, CancellationToken cancellationToken) =>
        await _context.VantaSettings.AddAsync(settings, cancellationToken);
}
