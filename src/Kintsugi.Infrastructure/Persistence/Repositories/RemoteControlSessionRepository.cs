using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class RemoteControlSessionRepository : IRemoteControlSessionRepository
{
    private readonly ApplicationDbContext _context;

    public RemoteControlSessionRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<RemoteControlSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.RemoteControlSessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<RemoteControlSession>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
        await _context.RemoteControlSessions
            .OrderByDescending(s => s.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(RemoteControlSession session, CancellationToken cancellationToken) =>
        await _context.RemoteControlSessions.AddAsync(session, cancellationToken);
}
