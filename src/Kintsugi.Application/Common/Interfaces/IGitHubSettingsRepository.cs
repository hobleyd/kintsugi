using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IGitHubSettingsRepository
{
    Task<GitHubSettings?> GetAsync(CancellationToken cancellationToken);

    Task AddAsync(GitHubSettings settings, CancellationToken cancellationToken);
}
