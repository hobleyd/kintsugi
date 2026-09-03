using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IVantaSettingsRepository
{
    Task<VantaSettings?> GetAsync(CancellationToken cancellationToken);

    Task AddAsync(VantaSettings settings, CancellationToken cancellationToken);
}
