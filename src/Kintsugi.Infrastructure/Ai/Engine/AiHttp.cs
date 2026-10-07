using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>What every adapter shares: authentication, Vertex AI addressing, and turning a
/// provider's error into a message an operator can act on.</summary>
public class AiHttp
{
    private readonly HttpClient _httpClient;
    private readonly GoogleCloudAccessTokenProvider _googleCloudTokens;

    public AiHttp(HttpClient httpClient, GoogleCloudAccessTokenProvider googleCloudTokens)
    {
        _httpClient = httpClient;
        // Research with several search rounds can run long; this is the ceiling per round trip,
        // the same one the per-provider client used before.
        _httpClient.Timeout = TimeSpan.FromSeconds(300);
        _googleCloudTokens = googleCloudTokens;
    }

    /// <summary>Vertex AI's host for a location: the bare global host for <c>global</c>, the
    /// regional one otherwise.</summary>
    public static string VertexBaseUrl(string project, string location) =>
        location == "global"
            ? $"https://aiplatform.googleapis.com/v1/projects/{project}/locations/global"
            : $"https://{location}-aiplatform.googleapis.com/v1/projects/{project}/locations/{location}";

    /// <summary>Azure's OpenAI endpoints take a key in an <c>api-key</c> header, not as a bearer.</summary>
    public static bool IsAzureHost(string? baseUrl) =>
        baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase));

    public async Task<JsonNode> PostJsonAsync(
        AiConnectionSettings connection, string url, JsonObject body, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        foreach (var header in extraHeaders ?? new Dictionary<string, string>())
        {
            request.Headers.Add(header.Key, header.Value);
        }

        await AuthenticateAsync(connection, request, cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException($"Could not reach the AI provider ({connection.Name}): {ex.Message}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ExternalServiceException(
                    $"AI provider request failed ({connection.Name}, HTTP {(int)response.StatusCode}): {Truncate(text, 1000)}");
            }

            try
            {
                return JsonNode.Parse(text) ?? throw new ExternalServiceException($"The AI provider ({connection.Name}) returned an empty body.");
            }
            catch (System.Text.Json.JsonException)
            {
                throw new ExternalServiceException($"The AI provider ({connection.Name}) returned something that is not JSON: {Truncate(text, 300)}");
            }
        }
    }

    private async Task AuthenticateAsync(AiConnectionSettings connection, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (connection.AuthMode)
        {
            case AiAuthMode.GoogleCloud:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _googleCloudTokens.GetAsync(cancellationToken));
                break;

            case AiAuthMode.ApiKey when !string.IsNullOrWhiteSpace(connection.ApiKey):
                switch (connection.Protocol)
                {
                    case AiWireProtocol.Anthropic:
                        request.Headers.Add("x-api-key", connection.ApiKey);
                        break;
                    case AiWireProtocol.Google:
                        request.Headers.Add("x-goog-api-key", connection.ApiKey);
                        break;
                    case AiWireProtocol.OpenAiChatCompletions or AiWireProtocol.OpenAiResponses when IsAzureHost(connection.BaseUrl):
                        request.Headers.Add("api-key", connection.ApiKey);
                        break;
                    default:
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
                        break;
                }

                break;
        }
    }

    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
