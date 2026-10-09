using Kintsugi.Application.AiSettings;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>The models.dev provider and model catalog, cached, fetched again when stale.</summary>
public interface IAiModelCatalog
{
    /// <summary>The catalog, from cache when fresh enough. Never throws for a failed fetch: it
    /// serves the last good copy marked stale, or an empty catalog when there has never been one —
    /// a custom endpoint needs no catalog at all.</summary>
    Task<AiCatalogDto> GetAsync(bool forceRefresh, CancellationToken cancellationToken);
}
