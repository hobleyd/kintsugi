using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class AuthenticationSettingsRepository : IAuthenticationSettingsRepository
{
    private readonly ApplicationDbContext _context;

    public AuthenticationSettingsRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<AuthenticationSettings?> GetAsync(CancellationToken cancellationToken) =>
        _context.AuthenticationSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(AuthenticationSettings settings, CancellationToken cancellationToken) =>
        await _context.AuthenticationSettings.AddAsync(settings, cancellationToken);
}
