namespace Kintsugi.Domain.Enums;

/// <summary>
/// The search service behind Kintsugi's own <c>web_search</c> tool — what lets a model with no
/// hosted search of its own (most OpenAI-compatible endpoints, a local
/// Ollama) still research a current version and download URL. Persisted by name.
/// </summary>
public enum WebSearchBackend
{
    /// <summary>No Kintsugi-run search. A model without hosted search is told it has no web access
    /// and must say so in the script it writes.</summary>
    None,

    /// <summary>Ollama's hosted <c>https://ollama.com/api/web_search</c> (an ollama.com API key).
    /// Also what <c>OLLAMA_WEB_API_KEY</c> configured before this setting existed.</summary>
    OllamaWeb,

    /// <summary>Tavily's search API (<c>https://api.tavily.com/search</c>).</summary>
    Tavily,

    /// <summary>Brave Search's API (<c>https://api.search.brave.com/res/v1/web/search</c>).</summary>
    Brave,

    /// <summary>A self-hosted SearXNG instance's JSON API — no key, no third party.</summary>
    SearXng,
}
