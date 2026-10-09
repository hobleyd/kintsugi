using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings;

/// <summary>A connection as the settings page sees it. The key is never returned —
/// <see cref="HasApiKey"/> says whether one is stored.</summary>
public record AiConnectionDto(
    Guid Id,
    string Name,
    string? CatalogProviderId,
    AiWireProtocol Protocol,
    string? BaseUrl,
    AiAuthMode AuthMode,
    bool HasApiKey,
    string? GoogleCloudProject,
    string? GoogleCloudLocation,
    bool UseHostedWebSearch)
{
    public static AiConnectionDto FromEntity(AiConnection c) => new(
        c.Id, c.Name, c.CatalogProviderId, c.Protocol, c.BaseUrl, c.AuthMode,
        !string.IsNullOrEmpty(c.ApiKey), c.GoogleCloudProject, c.GoogleCloudLocation, c.UseHostedWebSearch);
}

public record AiFeatureRouteDto(AiFeature Feature, Guid ConnectionId, string Model)
{
    public static AiFeatureRouteDto FromEntity(AiFeatureRoute r) => new(r.Feature, r.ConnectionId, r.Model);
}

/// <summary>Everything Routed mode's half of the settings page shows.</summary>
public record AiRoutingDto(IReadOnlyList<AiConnectionDto> Connections, IReadOnlyList<AiFeatureRouteDto> Routes);

/// <summary>What a connection test answered: the model's reply, or why there was none.</summary>
public record AiConnectionTestResultDto(bool Succeeded, string? Reply, string? Error, long ElapsedMilliseconds);

/// <summary>
/// The models.dev catalog, trimmed to what the settings page needs and to the providers some
/// adapter here can actually reach. See <c>ModelsDevCatalog</c>.
/// </summary>
public record AiCatalogDto(DateTimeOffset? FetchedAtUtc, bool IsStale, IReadOnlyList<AiCatalogProviderDto> Providers);

/// <param name="DefaultBaseUrl">What to prefill as the connection's base URL; null when the
/// protocol's own default applies (or, for Vertex, the URL is built from project and location).</param>
/// <param name="UsesGoogleCloud">A Vertex AI provider: authenticates with Google Cloud rather
/// than a key, and needs a project and location.</param>
public record AiCatalogProviderDto(
    string Id,
    string Name,
    AiWireProtocol Protocol,
    string? DefaultBaseUrl,
    bool RequiresApiKey,
    bool UsesGoogleCloud,
    string? DocUrl,
    IReadOnlyList<AiCatalogModelDto> Models);

/// <param name="Protocol">The model's own protocol, which differs from its provider's for Claude
/// listed under <c>google-vertex</c>.</param>
public record AiCatalogModelDto(
    string Id,
    string Name,
    AiWireProtocol Protocol,
    bool ToolCall,
    bool Reasoning,
    long? ContextLimit,
    string? ReleaseDate);
