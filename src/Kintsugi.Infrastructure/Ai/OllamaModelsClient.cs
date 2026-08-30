using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Infrastructure.Ai;

public class OllamaModelsClient : IOllamaModelsClient
{
    private readonly HttpClient _httpClient;

    public OllamaModelsClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/api/tags", UriKind.Absolute, out var tagsUri))
        {
            throw new ExternalServiceException("The Ollama endpoint URL is not valid.");
        }

        TagsResponse? response;
        try
        {
            response = await _httpClient.GetFromJsonAsync<TagsResponse>(tagsUri, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ExternalServiceException($"Could not reach the Ollama endpoint at {baseUrl}.", ex);
        }

        return response?.Models?
            .Select(m => m.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();
    }

    private class TagsResponse
    {
        [JsonPropertyName("models")]
        public List<TagsModel>? Models { get; set; }
    }

    private class TagsModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
