using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class GitHubSettingsRepository : IGitHubSettingsRepository
{
    private readonly ApplicationDbContext _context;

    public GitHubSettingsRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<GitHubSettings?> GetAsync(CancellationToken cancellationToken) =>
        _context.GitHubSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(GitHubSettings settings, CancellationToken cancellationToken) =>
        await _context.GitHubSettings.AddAsync(settings, cancellationToken);
}
