using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IAiAgentSettingsRepository
{
    Task<AiAgentSettings?> GetAsync(CancellationToken cancellationToken);
    Task AddAsync(AiAgentSettings settings, CancellationToken cancellationToken);
}
