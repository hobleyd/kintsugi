using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IAgentPackageRepository
{
    /// <summary>The newest published package for <paramref name="platform"/>, or null if none has
    /// been published yet.</summary>
    Task<AgentPackage?> GetLatestByPlatformAsync(string platform, CancellationToken cancellationToken);

    /// <summary>The newest published package for every platform that has at least one, one row
    /// each — what the Clients page and the public listing endpoint show.</summary>
    Task<IReadOnlyList<AgentPackage>> GetLatestPerPlatformAsync(CancellationToken cancellationToken);

    Task<AgentPackage?> GetByPlatformAndVersionAsync(string platform, string version, CancellationToken cancellationToken);

    Task AddAsync(AgentPackage package, CancellationToken cancellationToken);
}
