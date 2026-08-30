using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings;

/// <summary>The raw API key is never returned to the client; <see cref="HasApiKey"/> reports whether one is stored.</summary>
public record AiAgentSettingsDto(AiProvider Provider, string? Model, string? BaseUrl, bool IsEnabled, bool HasApiKey)
{
    public static AiAgentSettingsDto FromEntity(AiAgentSettings entity) =>
        new(entity.Provider, entity.Model, entity.BaseUrl, entity.IsEnabled, !string.IsNullOrEmpty(entity.ApiKey));

    public static AiAgentSettingsDto NotConfigured() =>
        new(AiProvider.Anthropic, null, null, IsEnabled: false, HasApiKey: false);
}
