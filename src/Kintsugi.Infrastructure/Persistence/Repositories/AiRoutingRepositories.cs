using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class AiConnectionRepository : IAiConnectionRepository
{
    private readonly ApplicationDbContext _context;

    public AiConnectionRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<AiConnection>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.AiConnections.ToListAsync(cancellationToken);

    public Task<AiConnection?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _context.AiConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task AddAsync(AiConnection connection, CancellationToken cancellationToken) =>
        await _context.AiConnections.AddAsync(connection, cancellationToken);

    public void Remove(AiConnection connection) => _context.AiConnections.Remove(connection);
}

public class AiFeatureRouteRepository : IAiFeatureRouteRepository
{
    private readonly ApplicationDbContext _context;

    public AiFeatureRouteRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<AiFeatureRoute>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.AiFeatureRoutes.ToListAsync(cancellationToken);

    public Task<AiFeatureRoute?> GetAsync(AiFeature feature, CancellationToken cancellationToken) =>
        _context.AiFeatureRoutes.FirstOrDefaultAsync(r => r.Feature == feature, cancellationToken);

    public async Task AddAsync(AiFeatureRoute route, CancellationToken cancellationToken) =>
        await _context.AiFeatureRoutes.AddAsync(route, cancellationToken);

    public void Remove(AiFeatureRoute route) => _context.AiFeatureRoutes.Remove(route);
}
