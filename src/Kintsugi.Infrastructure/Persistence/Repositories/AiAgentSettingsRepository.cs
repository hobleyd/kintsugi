using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class AiAgentSettingsRepository : IAiAgentSettingsRepository
{
    private readonly ApplicationDbContext _context;

    public AiAgentSettingsRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<AiAgentSettings?> GetAsync(CancellationToken cancellationToken) =>
        _context.AiAgentSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(AiAgentSettings settings, CancellationToken cancellationToken) =>
        await _context.AiAgentSettings.AddAsync(settings, cancellationToken);
}
