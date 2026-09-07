using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class AuditSettingsRepository : IAuditSettingsRepository
{
    private readonly ApplicationDbContext _context;

    public AuditSettingsRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<AuditSettings?> GetAsync(CancellationToken cancellationToken) =>
        _context.AuditSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(AuditSettings settings, CancellationToken cancellationToken) =>
        await _context.AuditSettings.AddAsync(settings, cancellationToken);
}
