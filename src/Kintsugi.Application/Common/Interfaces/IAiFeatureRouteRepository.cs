using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Common.Interfaces;

public interface IAiFeatureRouteRepository
{
    Task<IReadOnlyList<AiFeatureRoute>> GetAllAsync(CancellationToken cancellationToken);
    Task<AiFeatureRoute?> GetAsync(AiFeature feature, CancellationToken cancellationToken);
    Task AddAsync(AiFeatureRoute route, CancellationToken cancellationToken);
    void Remove(AiFeatureRoute route);
}
