using Microsoft.Extensions.Configuration;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai;

/// <summary>
/// Loads everything a call to the AI needs — the provider row, and in Routed mode every route with
/// its connection — into one <see cref="AiProviderSettings"/>, or null when AI is not configured.
/// </summary>
/// <remarks>
/// The one place that answers "is the AI configured?", so the upgrade-path scan and the
/// vulnerability run cannot disagree about it. Routed mode counts as configured only with a
/// <see cref="AiFeature.ScriptResearch"/> route whose connection still exists — the other features
/// fall back to it, and research has nothing to fall back to.
/// </remarks>
public class AiProviderSettingsResolver : IAiProviderSettingsResolver
{
    private readonly IAiAgentSettingsRepository _settingsRepository;
    private readonly IAiConnectionRepository _connectionRepository;
    private readonly IAiFeatureRouteRepository _routeRepository;
    private readonly IConfiguration _configuration;

    public AiProviderSettingsResolver(
        IAiAgentSettingsRepository settingsRepository,
        IAiConnectionRepository connectionRepository,
        IAiFeatureRouteRepository routeRepository,
        IConfiguration configuration)
    {
        _settingsRepository = settingsRepository;
        _connectionRepository = connectionRepository;
        _routeRepository = routeRepository;
        _configuration = configuration;
    }

    public async Task<AiProviderSettings?> ResolveAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsRepository.GetAsync(cancellationToken);
        if (settings is null || !settings.IsEnabled)
        {
            return null;
        }

        var webSearch = ResolveWebSearch(settings);

        if (settings.Provider != AiProvider.Routed)
        {
            return new AiProviderSettings(settings.Provider, settings.ApiKey, settings.BaseUrl, settings.Model) { WebSearch = webSearch };
        }

        var connections = (await _connectionRepository.GetAllAsync(cancellationToken)).ToDictionary(c => c.Id);
        var routes = new Dictionary<AiFeature, AiRoute>();
        foreach (var route in await _routeRepository.GetAllAsync(cancellationToken))
        {
            if (connections.TryGetValue(route.ConnectionId, out var connection))
            {
                routes[route.Feature] = new AiRoute(ToSettings(connection), route.Model);
            }
        }

        if (!routes.ContainsKey(AiFeature.ScriptResearch))
        {
            return null;
        }

        return new AiProviderSettings(AiProvider.Routed, null, null, null) { Routes = routes, WebSearch = webSearch };
    }

    public static AiConnectionSettings ToSettings(AiConnection connection) => new(
        connection.Name,
        connection.Protocol,
        connection.BaseUrl,
        connection.AuthMode,
        connection.ApiKey,
        connection.GoogleCloudProject,
        connection.GoogleCloudLocation,
        connection.UseHostedWebSearch);

    /// <summary>
    /// The configured backend, or — when none is configured — Ollama's, from the
    /// <c>OLLAMA_WEB_API_KEY</c> variable that drove Ollama's search before this setting existed,
    /// so a deployment relying on it keeps searching after the upgrade.
    /// </summary>
    private AiWebSearchSettings ResolveWebSearch(AiAgentSettings settings)
    {
        if (settings.WebSearchBackend != WebSearchBackend.None)
        {
            return new AiWebSearchSettings(settings.WebSearchBackend, settings.WebSearchApiKey, settings.WebSearchBaseUrl);
        }

        var legacyOllamaKey = _configuration["OLLAMA_WEB_API_KEY"];
        return string.IsNullOrWhiteSpace(legacyOllamaKey)
            ? AiWebSearchSettings.None
            : new AiWebSearchSettings(WebSearchBackend.OllamaWeb, legacyOllamaKey, null);
    }
}
