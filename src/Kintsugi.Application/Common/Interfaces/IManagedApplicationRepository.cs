using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>The applications another tool patches, and the list of tools that can own one.</summary>
public interface IManagedApplicationRepository
{
    Task<IReadOnlyList<ManagedApplication>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Every managed application's owner by name, compared without regard to case. Small by
    /// nature — a handful of products an administrator named — so callers that must skip them load it
    /// whole and look names up in memory.</summary>
    Task<IReadOnlyDictionary<string, string>> GetOwnersByNameAsync(CancellationToken cancellationToken);

    Task<ManagedApplication?> FindAsync(string applicationName, CancellationToken cancellationToken);

    Task AddAsync(ManagedApplication application, CancellationToken cancellationToken);

    void Remove(ManagedApplication application);

    /// <summary>Whether any managed application is owned by <paramref name="owner"/> — which is what
    /// stops an owner being removed from the list while it is in use.</summary>
    Task<bool> OwnerIsInUseAsync(string owner, CancellationToken cancellationToken);

    Task<IReadOnlyList<ManagedOwner>> GetOwnersAsync(CancellationToken cancellationToken);

    Task<ManagedOwner?> FindOwnerAsync(string name, CancellationToken cancellationToken);

    Task AddOwnerAsync(ManagedOwner owner, CancellationToken cancellationToken);

    void RemoveOwner(ManagedOwner owner);
}
