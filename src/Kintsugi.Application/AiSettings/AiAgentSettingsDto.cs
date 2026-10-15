using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings;

/// <summary>The raw API key is never returned to the client; <see cref="HasApiKey"/> reports whether one is stored.</summary>
/// <param name="WebSearchBackend">The search service behind Kintsugi's own <c>web_search</c> tool;
/// its key is never returned either — <paramref name="HasWebSearchApiKey"/> says whether one is stored.</param>
public record AiAgentSettingsDto(
    AiProvider Provider,
    string? Model,
    string? BaseUrl,
    bool IsEnabled,
    bool HasApiKey,
    WebSearchBackend WebSearchBackend = WebSearchBackend.None,
    bool HasWebSearchApiKey = false,
    string? WebSearchBaseUrl = null)
{
    public static AiAgentSettingsDto FromEntity(AiAgentSettings entity) =>
        new(entity.Provider, entity.Model, entity.BaseUrl, entity.IsEnabled, !string.IsNullOrEmpty(entity.ApiKey),
            entity.WebSearchBackend, !string.IsNullOrEmpty(entity.WebSearchApiKey), entity.WebSearchBaseUrl);

    public static AiAgentSettingsDto NotConfigured() =>
        new(AiProvider.Anthropic, null, null, IsEnabled: false, HasApiKey: false);
}
