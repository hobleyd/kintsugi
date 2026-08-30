using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IAuthenticationSettingsRepository
{
    Task<AuthenticationSettings?> GetAsync(CancellationToken cancellationToken);
    Task AddAsync(AuthenticationSettings settings, CancellationToken cancellationToken);
}
