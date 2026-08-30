using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IPatchingPolicySettingsRepository
{
    Task<PatchingPolicySettings?> GetAsync(CancellationToken cancellationToken);
    Task AddAsync(PatchingPolicySettings settings, CancellationToken cancellationToken);
}
