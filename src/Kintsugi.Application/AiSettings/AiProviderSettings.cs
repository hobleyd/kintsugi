using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings;

/// <summary>The connection details a call to the configured AI provider actually needs — a plain
/// value, as opposed to the <c>AiAgentSettings</c> domain entity, so callers outside the handler
/// that loaded it (e.g. a background research task) don't need to carry a tracked entity around.</summary>
/// <remarks>
/// In <see cref="AiProvider.Routed"/> mode the four positional fields are unused and
/// <see cref="Routes"/> carries a resolved connection and model per feature. Built by
/// <c>AiProviderSettingsResolver</c>, which is the one place that decides whether AI is
/// configured at all.
/// </remarks>
public record AiProviderSettings(AiProvider Provider, string? ApiKey, string? BaseUrl, string? Model)
{
    /// <summary>Per-feature routes, for <see cref="AiProvider.Routed"/>. Null otherwise.</summary>
    public IReadOnlyDictionary<AiFeature, AiRoute>? Routes { get; init; }

    /// <summary>The search service behind Kintsugi's own <c>web_search</c> tool.</summary>
    public AiWebSearchSettings WebSearch { get; init; } = AiWebSearchSettings.None;

    /// <summary>
    /// The route a feature runs on: its own, else the next feature down the chain — CPE suggestion
    /// falls back to repair, repair to research. Research has no fallback; Routed mode is not
    /// configured without it.
    /// </summary>
    public AiRoute? RouteFor(AiFeature feature)
    {
        if (Routes is null)
        {
            return null;
        }

        foreach (var candidate in FallbackChain(feature))
        {
            if (Routes.TryGetValue(candidate, out var route))
            {
                return route;
            }
        }

        return null;
    }

    private static IEnumerable<AiFeature> FallbackChain(AiFeature feature) => feature switch
    {
        AiFeature.CpeSuggestion => [AiFeature.CpeSuggestion, AiFeature.ScriptRepair, AiFeature.ScriptResearch],
        AiFeature.ScriptRepair => [AiFeature.ScriptRepair, AiFeature.ScriptResearch],
        _ => [AiFeature.ScriptResearch],
    };
}

/// <summary>A resolved <c>AiConnection</c>, decoupled from the tracked entity.</summary>
public record AiConnectionSettings(
    string Name,
    AiWireProtocol Protocol,
    string? BaseUrl,
    AiAuthMode AuthMode,
    string? ApiKey,
    string? GoogleCloudProject,
    string? GoogleCloudLocation,
    bool UseHostedWebSearch);

/// <summary>One feature's connection and model.</summary>
public record AiRoute(AiConnectionSettings Connection, string Model);

/// <summary>The search service behind Kintsugi's own <c>web_search</c> tool.</summary>
public record AiWebSearchSettings(WebSearchBackend Backend, string? ApiKey, string? BaseUrl)
{
    public static readonly AiWebSearchSettings None = new(WebSearchBackend.None, null, null);

    public bool IsConfigured => Backend != WebSearchBackend.None;
}
