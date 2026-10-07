using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kintsugi.Application.AiSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>
/// The search behind Kintsugi's own <c>web_search</c> tool, normalized to one result shape whatever
/// the backend: a JSON array of <c>{title, url, content}</c>, five results, each snippet capped so a
/// round of searching cannot crowd the script out of the model's context.
/// </summary>
public class WebSearchClient
{
    public const int MaxResults = 5;
    public const int MaxSnippetChars = 800;

    private readonly HttpClient _httpClient;

    public WebSearchClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Results as JSON text, or a sentence saying why there are none — the answer goes back
    /// to the model as a tool result, so a failed search must not throw.</summary>
    public async Task<string> SearchAsync(AiWebSearchSettings settings, string query, CancellationToken cancellationToken)
    {
        try
        {
            var results = settings.Backend switch
            {
                WebSearchBackend.OllamaWeb => await OllamaAsync(settings, query, cancellationToken),
                WebSearchBackend.Tavily => await TavilyAsync(settings, query, cancellationToken),
                WebSearchBackend.Brave => await BraveAsync(settings, query, cancellationToken),
                WebSearchBackend.SearXng => await SearXngAsync(settings, query, cancellationToken),
                _ => null,
            };

            if (results is null)
            {
                return "Web search is not configured on this server.";
            }

            return new JsonArray(results.Take(MaxResults).Select(r => (JsonNode)new JsonObject
            {
                ["title"] = r.Title,
                ["url"] = r.Url,
                ["content"] = r.Content is { Length: > MaxSnippetChars } c ? c[..MaxSnippetChars] : r.Content,
            }).ToArray()).ToJsonString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return $"Web search failed: {ex.Message}";
        }
    }

    private async Task<List<Result>> OllamaAsync(AiWebSearchSettings s, string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://ollama.com/api/web_search")
        {
            Content = JsonContent(new JsonObject { ["query"] = query, ["max_results"] = MaxResults }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);
        var body = await SendAsync(request, ct);
        return Read(body["results"], "title", "url", "content");
    }

    private async Task<List<Result>> TavilyAsync(AiWebSearchSettings s, string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = JsonContent(new JsonObject { ["query"] = query, ["max_results"] = MaxResults }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);
        var body = await SendAsync(request, ct);
        return Read(body["results"], "title", "url", "content");
    }

    private async Task<List<Result>> BraveAsync(AiWebSearchSettings s, string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={MaxResults}");
        request.Headers.Add("X-Subscription-Token", s.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var body = await SendAsync(request, ct);
        return Read(body["web"]?["results"], "title", "url", "description");
    }

    private async Task<List<Result>> SearXngAsync(AiWebSearchSettings s, string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{s.BaseUrl!.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json");
        var body = await SendAsync(request, ct);
        return Read(body["results"], "title", "url", "content");
    }

    private async Task<JsonNode> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {AiHttp.Truncate(text, 200)}");
        }

        return JsonNode.Parse(text) ?? new JsonObject();
    }

    private static List<Result> Read(JsonNode? array, string title, string url, string content) =>
        (array as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(r => new Result(r[title]?.ToString(), r[url]?.ToString(), r[content]?.ToString()))
            .Where(r => !string.IsNullOrWhiteSpace(r.Url))
            .ToList();

    private static StringContent JsonContent(JsonObject body) =>
        new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    private sealed record Result(string? Title, string? Url, string? Content);
}
