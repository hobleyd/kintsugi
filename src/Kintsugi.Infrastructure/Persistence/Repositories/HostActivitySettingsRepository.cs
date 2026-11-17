using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class HostActivitySettingsRepository : IHostActivitySettingsRepository
{
    private readonly ApplicationDbContext _context;

    public HostActivitySettingsRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<HostActivitySettings?> GetAsync(CancellationToken cancellationToken) =>
        _context.HostActivitySettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(HostActivitySettings settings, CancellationToken cancellationToken) =>
        await _context.HostActivitySettings.AddAsync(settings, cancellationToken);
}
