using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;
using Kintsugi.Infrastructure.Persistence;

namespace Kintsugi.Infrastructure.Ai;

/// <summary>
/// The models.dev catalog (<c>https://models.dev/api.json</c>), trimmed to what the settings page
/// needs and to what an adapter here can reach, cached in memory and in the database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reference data, not configuration.</b> A connection copies what it needs out of the catalog
/// when it is saved, so the catalog changing — or this server never reaching models.dev at all —
/// cannot break a working connection. Stale-while-revalidate: a copy older than
/// <see cref="FreshFor"/> is refreshed on read, and a failed refresh serves the last good copy
/// marked stale rather than an error. A custom endpoint needs no catalog.
/// </para>
/// <para>
/// <b>The protocol is derived per model</b>, from the AI SDK package models.dev names (<c>npm</c>),
/// because a provider's own is not enough: <c>google-vertex</c> lists Claude models whose
/// <c>provider.npm</c> is <c>@ai-sdk/google-vertex/anthropic</c>. Anything OpenAI-shaped — most
/// providers — maps to <see cref="AiWireProtocol.OpenAiChatCompletions"/>; a package with no
/// adapter here (Bedrock) drops the model, and a provider left with none drops out.
/// </para>
/// </remarks>
public class ModelsDevCatalog : IAiModelCatalog
{
    public const string CatalogUrl = "https://models.dev/api.json";
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);

    /// <summary>OpenAI-compatible providers whose models.dev entry names an SDK package rather than
    /// an <c>api</c> base URL. Anything not here and without an <c>api</c> needs one typed in.</summary>
    internal static readonly IReadOnlyDictionary<string, string> KnownBaseUrls = new Dictionary<string, string>
    {
        ["openai"] = "https://api.openai.com/v1",
        ["groq"] = "https://api.groq.com/openai/v1",
        ["mistral"] = "https://api.mistral.ai/v1",
        ["xai"] = "https://api.x.ai/v1",
        ["deepseek"] = "https://api.deepseek.com/v1",
        ["togetherai"] = "https://api.together.xyz/v1",
        ["fireworks-ai"] = "https://api.fireworks.ai/inference/v1",
        ["cerebras"] = "https://api.cerebras.ai/v1",
        ["perplexity"] = "https://api.perplexity.ai",
    };

    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static AiCatalogDto? _memory;

    private readonly HttpClient _httpClient;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ModelsDevCatalog> _logger;

    public ModelsDevCatalog(HttpClient httpClient, ApplicationDbContext db, ILogger<ModelsDevCatalog> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _db = db;
        _logger = logger;
    }

    public async Task<AiCatalogDto> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await Lock.WaitAsync(cancellationToken);
        try
        {
            _memory ??= await LoadCachedAsync(cancellationToken);
            if (!forceRefresh && _memory?.FetchedAtUtc is { } fetched && DateTimeOffset.UtcNow - fetched < FreshFor)
            {
                return _memory;
            }

            try
            {
                using var response = await _httpClient.GetAsync(CatalogUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                var raw = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject
                    ?? throw new JsonException("models.dev returned something other than an object.");

                _memory = Map(raw, DateTimeOffset.UtcNow);
                await SaveCachedAsync(_memory, cancellationToken);
                return _memory;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Could not refresh the models.dev catalog; serving the cached copy. {Reason}", ex.Message);
                return _memory is null
                    ? new AiCatalogDto(null, IsStale: true, [])
                    : _memory with { IsStale = true };
            }
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>The trimming and protocol derivation, as a pure function; public for tests.</summary>
    public static AiCatalogDto Map(JsonObject raw, DateTimeOffset fetchedAtUtc)
    {
        var providers = new List<AiCatalogProviderDto>();
        foreach (var (id, node) in raw)
        {
            if (node is not JsonObject provider)
            {
                continue;
            }

            var providerNpm = provider["npm"]?.ToString();
            var providerProtocol = ProtocolFor(providerNpm, shape: null);
            var models = new List<AiCatalogModelDto>();
            foreach (var (modelId, modelNode) in provider["models"] as JsonObject ?? new JsonObject())
            {
                if (modelNode is not JsonObject model || model["status"]?.ToString() == "deprecated")
                {
                    continue;
                }

                var modelNpm = model["provider"]?["npm"]?.ToString() ?? providerNpm;
                var protocol = ProtocolFor(modelNpm, model["provider"]?["shape"]?.ToString());
                if (protocol is null)
                {
                    continue;
                }

                models.Add(new AiCatalogModelDto(
                    model["id"]?.ToString() ?? modelId,
                    model["name"]?.ToString() ?? modelId,
                    protocol.Value,
                    AsBool(model["tool_call"]),
                    AsBool(model["reasoning"]),
                    AsLong(model["limit"]?["context"]),
                    model["release_date"]?.ToString()));
            }

            if (models.Count == 0)
            {
                continue;
            }

            var usesGoogleCloud = providerNpm?.StartsWith("@ai-sdk/google-vertex", StringComparison.Ordinal) == true;
            var env = (provider["env"] as JsonArray)?.Count ?? 0;
            providers.Add(new AiCatalogProviderDto(
                id,
                provider["name"]?.ToString() ?? id,
                providerProtocol ?? models[0].Protocol,
                usesGoogleCloud ? null : provider["api"]?.ToString() ?? KnownBaseUrls.GetValueOrDefault(id),
                RequiresApiKey: !usesGoogleCloud && env > 0,
                usesGoogleCloud,
                provider["doc"]?.ToString(),
                models.OrderByDescending(m => m.ReleaseDate).ThenBy(m => m.Name).ToList()));
        }

        return new AiCatalogDto(fetchedAtUtc, IsStale: false, providers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// The protocol an AI SDK package speaks, or null for one no adapter here can reach. Unknown
    /// packages default to the OpenAI-compatible shape, which is what nearly all of them are.
    /// </summary>
    internal static AiWireProtocol? ProtocolFor(string? npm, string? shape) => npm switch
    {
        "@ai-sdk/anthropic" or "@ai-sdk/google-vertex/anthropic" => AiWireProtocol.Anthropic,
        "@ai-sdk/google" or "@ai-sdk/google-vertex" => AiWireProtocol.Google,
        "@ai-sdk/openai" => shape == "completions" ? AiWireProtocol.OpenAiChatCompletions : AiWireProtocol.OpenAiResponses,
        "@ai-sdk/amazon-bedrock" => null,
        _ => shape == "responses" ? AiWireProtocol.OpenAiResponses : AiWireProtocol.OpenAiChatCompletions,
    };

    // models.dev is community-maintained JSON; a field of an unexpected type is skipped, not fatal.
    private static bool AsBool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static long? AsLong(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d : null;

    private async Task<AiCatalogDto?> LoadCachedAsync(CancellationToken cancellationToken)
    {
        var row = await _db.AiCatalogCache.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiCatalogDto>(row.Json, CacheJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SaveCachedAsync(AiCatalogDto catalog, CancellationToken cancellationToken)
    {
        var row = await _db.AiCatalogCache.FirstOrDefaultAsync(cancellationToken);
        var json = JsonSerializer.Serialize(catalog, CacheJson);
        if (row is null)
        {
            _db.AiCatalogCache.Add(new AiCatalogCacheEntry { Id = 1, Json = json, FetchedAtUtc = catalog.FetchedAtUtc ?? DateTimeOffset.UtcNow });
        }
        else
        {
            row.Json = json;
            row.FetchedAtUtc = catalog.FetchedAtUtc ?? DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>The one row holding the last good catalog, trimmed. Infrastructure-only: nothing in the
/// domain has an opinion about it.</summary>
public class AiCatalogCacheEntry
{
    public int Id { get; set; }
    public DateTimeOffset FetchedAtUtc { get; set; }
    public string Json { get; set; } = string.Empty;
}
