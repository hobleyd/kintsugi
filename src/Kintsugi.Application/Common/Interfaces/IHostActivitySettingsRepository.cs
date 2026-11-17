using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IHostActivitySettingsRepository
{
    Task<HostActivitySettings?> GetAsync(CancellationToken cancellationToken);
    Task AddAsync(HostActivitySettings settings, CancellationToken cancellationToken);
}
