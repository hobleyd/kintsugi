using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IAuditSettingsRepository
{
    Task<AuditSettings?> GetAsync(CancellationToken cancellationToken);

    Task AddAsync(AuditSettings settings, CancellationToken cancellationToken);
}
