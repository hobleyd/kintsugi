using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Singleton configuration describing which AI agent (Anthropic, OpenAI, a local Ollama
/// endpoint, or a local Goose CLI installation) the system should use, and how to reach it.
/// </summary>
public class AiAgentSettings : BaseEntity
{
    public AiProvider Provider { get; private set; }
    public string? ApiKey { get; private set; }
    public string? BaseUrl { get; private set; }
    public string? Model { get; private set; }
    public bool IsEnabled { get; private set; }

    private AiAgentSettings()
    {
    }

    public static AiAgentSettings Create(AiProvider provider, string? apiKey, string? baseUrl, string? model, bool isEnabled)
    {
        var settings = new AiAgentSettings();
        settings.Apply(provider, apiKey, baseUrl, model, isEnabled);
        return settings;
    }

    public void Update(AiProvider provider, string? apiKey, string? baseUrl, string? model, bool isEnabled)
    {
        Apply(provider, apiKey, baseUrl, model, isEnabled);
        MarkUpdated();
    }

    // Blank apiKey on an update means "keep the currently stored key" rather than clear it,
    // since the UI never round-trips the real key back to the browser.
    private void Apply(AiProvider provider, string? apiKey, string? baseUrl, string? model, bool isEnabled)
    {
        Provider = provider;

        if (provider == AiProvider.Ollama)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new DomainException("A base URL is required when connecting to a local Ollama endpoint.");
            }

            ApiKey = null;
        }
        else if (provider == AiProvider.GooseCli)
        {
            // Goose manages its own provider credentials via its own config/environment — this
            // system only needs to know how to reach it (baseUrl is the base URL of a `goose
            // serve` instance; blank uses Goose's own default local address) and, optionally,
            // which model to ask it to use.
            ApiKey = null;
        }
        else
        {
            var resolvedApiKey = string.IsNullOrWhiteSpace(apiKey) ? ApiKey : apiKey;

            if (string.IsNullOrWhiteSpace(resolvedApiKey))
            {
                throw new DomainException($"An API key is required for {provider}.");
            }

            ApiKey = resolvedApiKey;
        }

        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl;
        Model = string.IsNullOrWhiteSpace(model) ? null : model;
        IsEnabled = isEnabled;
    }
}
