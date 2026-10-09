using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One configured way of reaching a model: a wire protocol, where to send it, and how to
/// authenticate. Picked from the models.dev catalog (<see cref="CatalogProviderId"/> set) or entered
/// by hand as a custom endpoint. <see cref="AiFeatureRoute"/> points each AI feature at one of these
/// and a model.
/// </summary>
/// <remarks>
/// The key is stored as written, like every other credential in this database — see
/// <c>GitHubSettings</c>'s remarks — and never returned by a route. A connection that authenticates
/// with <see cref="AiAuthMode.GoogleCloud"/> stores no secret at all.
/// </remarks>
public class AiConnection : BaseEntity
{
    public string Name { get; private set; } = string.Empty;

    /// <summary>The models.dev provider id this was created from (<c>google-vertex</c>,
    /// <c>openrouter</c>, …), or null for a custom endpoint. Informational: everything needed to
    /// make a call is in the fields below, so a catalog entry that later changes or disappears
    /// cannot break a working connection.</summary>
    public string? CatalogProviderId { get; private set; }

    public AiWireProtocol Protocol { get; private set; }

    /// <summary>Where requests go — e.g. <c>https://openrouter.ai/api/v1</c>. Null means the
    /// protocol's own default (<c>api.openai.com</c>, <c>api.anthropic.com</c>, Google AI Studio)
    /// or, under <see cref="AiAuthMode.GoogleCloud"/>, the Vertex AI endpoint for
    /// <see cref="GoogleCloudLocation"/>.</summary>
    public string? BaseUrl { get; private set; }

    public AiAuthMode AuthMode { get; private set; }
    public string? ApiKey { get; private set; }

    /// <summary>The Vertex AI project, for <see cref="AiAuthMode.GoogleCloud"/>.</summary>
    public string? GoogleCloudProject { get; private set; }

    /// <summary>The Vertex AI location — a region such as <c>australia-southeast1</c>, or
    /// <c>global</c>. Where inference runs, which is a data-residency decision, not a
    /// performance one.</summary>
    public string? GoogleCloudLocation { get; private set; }

    /// <summary>
    /// Use the provider's own hosted web search (Anthropic's <c>web_search</c>, OpenAI's on the
    /// Responses API, Gemini's Google Search grounding) when researching. When false — or for a
    /// protocol that has none — research uses Kintsugi's own <c>web_search</c>/<c>web_fetch</c>
    /// tools against the configured <see cref="WebSearchBackend"/>. A provider that rejects its own
    /// hosted tool (a model or a region without it) is fixed by turning this off.
    /// </summary>
    public bool UseHostedWebSearch { get; private set; }

    private AiConnection()
    {
    }

    public static AiConnection Create(
        string name, string? catalogProviderId, AiWireProtocol protocol, string? baseUrl, AiAuthMode authMode,
        string? apiKey, string? googleCloudProject, string? googleCloudLocation, bool useHostedWebSearch)
    {
        var connection = new AiConnection();
        connection.Apply(name, catalogProviderId, protocol, baseUrl, authMode, apiKey, googleCloudProject, googleCloudLocation, useHostedWebSearch);
        return connection;
    }

    /// <summary>A blank <paramref name="apiKey"/> keeps the stored one, as on every settings page
    /// here: the page never receives the real value, so it cannot send it back unchanged.</summary>
    public void Update(
        string name, string? catalogProviderId, AiWireProtocol protocol, string? baseUrl, AiAuthMode authMode,
        string? apiKey, string? googleCloudProject, string? googleCloudLocation, bool useHostedWebSearch)
    {
        Apply(name, catalogProviderId, protocol, baseUrl, authMode, apiKey, googleCloudProject, googleCloudLocation, useHostedWebSearch);
        MarkUpdated();
    }

    private void Apply(
        string name, string? catalogProviderId, AiWireProtocol protocol, string? baseUrl, AiAuthMode authMode,
        string? apiKey, string? googleCloudProject, string? googleCloudLocation, bool useHostedWebSearch)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A connection needs a name.");
        }

        var trimmedBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/');
        if (trimmedBaseUrl is not null
            && (!Uri.TryCreate(trimmedBaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
        {
            throw new DomainException("The base URL must be an absolute http(s) URL.");
        }

        // Ollama and the generic OpenAI-compatible shape have no public default host to fall back
        // on — a connection that names neither has nowhere to send anything.
        if (trimmedBaseUrl is null && authMode != AiAuthMode.GoogleCloud
            && protocol is AiWireProtocol.Ollama)
        {
            throw new DomainException("An Ollama connection needs its base URL.");
        }

        var resolvedKey = string.IsNullOrWhiteSpace(apiKey) ? ApiKey : apiKey.Trim();
        switch (authMode)
        {
            case AiAuthMode.ApiKey:
                if (string.IsNullOrWhiteSpace(resolvedKey))
                {
                    throw new DomainException("This connection authenticates with an API key, and none is stored.");
                }

                ApiKey = resolvedKey;
                break;

            case AiAuthMode.GoogleCloud:
                if (protocol is AiWireProtocol.OpenAiResponses or AiWireProtocol.Ollama)
                {
                    throw new DomainException(
                        "Google Cloud authentication works with the Google, Anthropic (Claude on Vertex) and OpenAI-compatible protocols only.");
                }

                // Vertex URLs are built from these, except for an OpenAI-compatible connection,
                // which names its Vertex endpoint in full in BaseUrl.
                if (protocol != AiWireProtocol.OpenAiChatCompletions
                    && (string.IsNullOrWhiteSpace(googleCloudProject) || string.IsNullOrWhiteSpace(googleCloudLocation)))
                {
                    throw new DomainException("Vertex AI needs a Google Cloud project and location.");
                }

                if (protocol == AiWireProtocol.OpenAiChatCompletions && trimmedBaseUrl is null)
                {
                    throw new DomainException("An OpenAI-compatible connection to Vertex AI needs its full endpoint as the base URL.");
                }

                // Nothing secret is kept: the token comes from the platform per call.
                ApiKey = null;
                break;

            default:
                ApiKey = null;
                break;
        }

        Name = name.Trim();
        CatalogProviderId = string.IsNullOrWhiteSpace(catalogProviderId) ? null : catalogProviderId.Trim();
        Protocol = protocol;
        BaseUrl = trimmedBaseUrl;
        AuthMode = authMode;
        GoogleCloudProject = authMode == AiAuthMode.GoogleCloud && !string.IsNullOrWhiteSpace(googleCloudProject) ? googleCloudProject.Trim() : null;
        GoogleCloudLocation = authMode == AiAuthMode.GoogleCloud && !string.IsNullOrWhiteSpace(googleCloudLocation) ? googleCloudLocation.Trim() : null;
        // Only three protocols have a hosted search tool to turn on.
        UseHostedWebSearch = useHostedWebSearch
            && protocol is AiWireProtocol.Anthropic or AiWireProtocol.OpenAiResponses or AiWireProtocol.Google;
    }
}
