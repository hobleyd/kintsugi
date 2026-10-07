using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Moq;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;
using Kintsugi.Infrastructure.Ai;
using Kintsugi.Infrastructure.Ai.Engine;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// Answers by URL rather than by order, and records every request with its body — the engine's
/// tests care which endpoint was called with what, not the sequence the research client cares about.
/// </summary>
internal sealed class RoutingHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, string, HttpResponseMessage> Respond)> _routes = new();
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

    public RoutingHandler On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        _routes.Add((match, respond));
        return this;
    }

    public RoutingHandler OnJson(Func<HttpRequestMessage, bool> match, Func<string, object> respond) =>
        On(match, (_, body) => Json(respond(body)));

    public static HttpResponseMessage Json(object payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        foreach (var (match, respond) in _routes)
        {
            if (match(request))
            {
                return respond(request, body);
            }
        }

        throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
    }
}

public class AiEngineAdapterTests
{
    private static AiConnectionSettings Connection(
        AiWireProtocol protocol, string? baseUrl = null, AiAuthMode auth = AiAuthMode.ApiKey, string? key = "k",
        string? project = null, string? location = null, bool hosted = false) =>
        new("test", protocol, baseUrl, auth, key, project, location, hosted);

    [Fact]
    public async Task ChatCompletions_SendsBearerToTheBaseUrl_AndReadsTheMessage()
    {
        var handler = new RoutingHandler().OnJson(
            r => r.RequestUri!.ToString() == "https://openrouter.ai/api/v1/chat/completions",
            _ => new { choices = new[] { new { message = new { role = "assistant", content = "hello" } } } });

        var text = await TestAiEngine.Over(handler).CompleteAsync(
            new AiRoute(Connection(AiWireProtocol.OpenAiChatCompletions, "https://openrouter.ai/api/v1/"), "meta/llama"), "hi", null, CancellationToken.None);

        Assert.Equal("hello", text);
        var (request, body) = handler.Calls.Single();
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("meta/llama", JsonNode.Parse(body)!["model"]!.GetValue<string>());
        // No max_tokens: reasoning models reject it and half the compatible servers lack its successor.
        Assert.Null(JsonNode.Parse(body)!["max_tokens"]);
    }

    [Fact]
    public async Task ChatCompletions_OnAzure_SendsTheKeyAsApiKeyHeader()
    {
        var handler = new RoutingHandler().OnJson(_ => true,
            _ => new { choices = new[] { new { message = new { role = "assistant", content = "ok" } } } });

        await TestAiEngine.Over(handler).CompleteAsync(
            new AiRoute(Connection(AiWireProtocol.OpenAiChatCompletions, "https://acme.openai.azure.com/openai/v1"), "gpt-4.1"), "hi", null, CancellationToken.None);

        var request = handler.Calls.Single().Request;
        Assert.True(request.Headers.Contains("api-key"));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Google_OnVertex_UsesAMetadataTokenAndTheRegionalPublisherUrl()
    {
        var handler = new RoutingHandler()
            .OnJson(r => r.RequestUri!.Host == "metadata.google.internal", _ => new { access_token = "ya29.token", expires_in = 3600 })
            .OnJson(r => r.RequestUri!.Host == "australia-southeast1-aiplatform.googleapis.com",
                _ => new { candidates = new[] { new { content = new { role = "model", parts = new object[] { new { text = "thinking…", thought = true }, new { text = "answer" } } } } } });

        var text = await TestAiEngine.Over(handler).CompleteAsync(
            new AiRoute(Connection(AiWireProtocol.Google, auth: AiAuthMode.GoogleCloud, key: null, project: "corp-tools", location: "australia-southeast1"), "gemini-2.5-pro"),
            "hi", null, CancellationToken.None);

        // A thought part is the model's reasoning, never the reply.
        Assert.Equal("answer", text);
        var metadata = handler.Calls[0].Request;
        Assert.Equal("Google", metadata.Headers.GetValues("Metadata-Flavor").Single());
        var vertex = handler.Calls[1].Request;
        Assert.Equal(
            "https://australia-southeast1-aiplatform.googleapis.com/v1/projects/corp-tools/locations/australia-southeast1/publishers/google/models/gemini-2.5-pro:generateContent",
            vertex.RequestUri!.ToString());
        Assert.Equal("ya29.token", vertex.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Google_NeverCapsOutput_BecauseTheCapIncludesThinking()
    {
        var handler = new RoutingHandler().OnJson(_ => true,
            _ => new { candidates = new[] { new { content = new { role = "model", parts = new[] { new { text = "#!/bin/bash" } } } } } });

        await TestAiEngine.Over(handler).ResearchAsync(
            new AiRoute(Connection(AiWireProtocol.Google, hosted: true), "gemini-2.5-flash"),
            AiWebSearchSettings.None, "research acrobat", 8192, CancellationToken.None);

        // 8192 was spent entirely on thinking for a large application, leaving no answer at all.
        Assert.Null(JsonNode.Parse(handler.Calls.Single().Body)!["generationConfig"]?["maxOutputTokens"]);
    }

    [Fact]
    public async Task Google_RunningOutOfBudgetBeforeAnswering_SaysSo()
    {
        var handler = new RoutingHandler().OnJson(_ => true, _ => new { candidates = new[] { new { finishReason = "MAX_TOKENS" } } });

        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => TestAiEngine.Over(handler).CompleteAsync(
            new AiRoute(Connection(AiWireProtocol.Google), "gemini-2.5-flash"), "hi", null, CancellationToken.None));

        Assert.Contains("thinking", ex.Message);
    }

    [Fact]
    public async Task Anthropic_OnVertex_MovesTheModelIntoTheUrlAndTheVersionIntoTheBody()
    {
        var handler = new RoutingHandler()
            .OnJson(r => r.RequestUri!.Host == "metadata.google.internal", _ => new { access_token = "ya29.token", expires_in = 3600 })
            .OnJson(r => r.RequestUri!.Host == "aiplatform.googleapis.com", _ => new { content = new[] { new { type = "text", text = "ok" } } });

        await TestAiEngine.Over(handler).CompleteAsync(
            new AiRoute(Connection(AiWireProtocol.Anthropic, auth: AiAuthMode.GoogleCloud, key: null, project: "corp-tools", location: "global"), "claude-sonnet-4-5@20250929"),
            "hi", null, CancellationToken.None);

        var (request, body) = handler.Calls[1];
        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/corp-tools/locations/global/publishers/anthropic/models/claude-sonnet-4-5%4020250929:rawPredict",
            request.RequestUri!.AbsoluteUri);
        var json = JsonNode.Parse(body)!;
        Assert.Null(json["model"]);
        Assert.Equal("vertex-2023-10-16", json["anthropic_version"]!.GetValue<string>());
        Assert.False(request.Headers.Contains("anthropic-version"));
    }

    [Fact]
    public async Task ResearchWithHostedSearch_OffersOnlyTheProvidersOwnTool()
    {
        var handler = new RoutingHandler().OnJson(_ => true,
            _ => new { candidates = new[] { new { content = new { role = "model", parts = new[] { new { text = "#!/bin/bash" } } } } } });

        await TestAiEngine.Over(handler).ResearchAsync(
            new AiRoute(Connection(AiWireProtocol.Google, hosted: true), "gemini-2.5-pro"),
            new AiWebSearchSettings(WebSearchBackend.Tavily, "tvly", null), "research firefox", 8192, CancellationToken.None);

        // Grounding and function tools are never mixed: most Gemini models refuse the request.
        var tools = JsonNode.Parse(handler.Calls.Single().Body)!["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.NotNull(tools[0]!["googleSearch"]);
    }

    [Fact]
    public async Task ResearchWithoutHostedSearch_RunsKintsugisToolsUntilTheModelAnswers()
    {
        var round = 0;
        var handler = new RoutingHandler()
            .OnJson(r => r.RequestUri!.Host == "api.tavily.com",
                _ => new { results = new[] { new { title = "Firefox 140", url = "https://www.mozilla.org/firefox/140/releasenotes/", content = "Firefox 140.0 was released" } } })
            .OnJson(r => r.RequestUri!.Host == "llm.example.com", _ => ++round == 1
                ? new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                role = "assistant",
                                content = (string?)null,
                                tool_calls = new[] { new { id = "c1", type = "function", function = new { name = "web_search", arguments = "{\"query\":\"firefox latest\"}" } } },
                            },
                        },
                    },
                }
                : new { choices = new[] { new { message = new { role = "assistant", content = "#!/bin/bash\necho 140.0", tool_calls = (object[]?)null } } } });

        var text = await TestAiEngine.Over(handler).ResearchAsync(
            new AiRoute(Connection(AiWireProtocol.OpenAiChatCompletions, "https://llm.example.com/v1"), "local"),
            new AiWebSearchSettings(WebSearchBackend.Tavily, "tvly-key", null), "research firefox", null, CancellationToken.None);

        Assert.Equal("#!/bin/bash\necho 140.0", text);
        var llmCalls = handler.Calls.Where(c => c.Request.RequestUri!.Host == "llm.example.com").ToList();
        var first = JsonNode.Parse(llmCalls[0].Body)!;
        Assert.Contains("web_search", first["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal(2, first["tools"]!.AsArray().Count);

        // The second round carries the assistant's tool call back verbatim, then the tool result.
        var second = JsonNode.Parse(llmCalls[1].Body)!["messages"]!.AsArray();
        Assert.Equal("c1", second[1]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("tool", second[2]!["role"]!.GetValue<string>());
        Assert.Contains("Firefox 140", second[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task ResearchWithNoSearchAtAll_OffersNoToolsAndAsksForAWarning()
    {
        var handler = new RoutingHandler().OnJson(_ => true, _ => new { message = new { role = "assistant", content = "#!/bin/bash" } });

        await TestAiEngine.Over(handler).ResearchAsync(
            new AiRoute(Connection(AiWireProtocol.Ollama, "http://ollama:11434", AiAuthMode.None, null), "llama3"),
            AiWebSearchSettings.None, "research firefox", null, CancellationToken.None);

        // A local model without tool support errors on a request carrying tools, so none are sent.
        var body = JsonNode.Parse(handler.Calls.Single().Body)!;
        Assert.Null(body["tools"]);
        Assert.Contains("# WARNING", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task AModelThatNeverStopsCallingTools_IsMadeToAnswerOnceTheBudgetIsSpent()
    {
        var handler = new RoutingHandler()
            .OnJson(r => r.RequestUri!.Host == "api.tavily.com", _ => new { results = Array.Empty<object>() })
            .OnJson(r => r.RequestUri!.Host == "llm.example.com", body => JsonNode.Parse(body)!["tools"] is null
                ? new { choices = new[] { new { message = new { role = "assistant", content = "final", tool_calls = (object[]?)null } } } }
                : new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                role = "assistant",
                                content = (string?)null,
                                tool_calls = new[] { new { id = "c", type = "function", function = new { name = "web_search", arguments = "{\"query\":\"again\"}" } } },
                            },
                        },
                    },
                });

        var text = await TestAiEngine.Over(handler).ResearchAsync(
            new AiRoute(Connection(AiWireProtocol.OpenAiChatCompletions, "https://llm.example.com/v1"), "m"),
            new AiWebSearchSettings(WebSearchBackend.Tavily, "k", null), "p", null, CancellationToken.None);

        Assert.Equal("final", text);
        Assert.Equal(AiEngine.MaxToolRounds + 1, handler.Calls.Count(c => c.Request.RequestUri!.Host == "llm.example.com"));
    }

    [Fact]
    public async Task AProviderError_SurfacesItsOwnBody()
    {
        var handler = new RoutingHandler().On(_ => true, (_, _) => RoutingHandler.Json(new { error = new { message = "model not found: gpt-9" } }, HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => TestAiEngine.Over(handler).CompleteAsync(
            new AiRoute(Connection(AiWireProtocol.OpenAiResponses), "gpt-9"), "hi", null, CancellationToken.None));

        Assert.Contains("model not found: gpt-9", ex.Message);
        Assert.Contains("HTTP 404", ex.Message);
    }

    [Fact]
    public async Task Probe_ReportsFailureWithoutThrowing()
    {
        var handler = new RoutingHandler().On(_ => true, (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("bad key") });

        var result = await TestAiEngine.Over(handler).ProbeAsync(Connection(AiWireProtocol.Anthropic), "claude", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("bad key", result.Error);
    }
}

public class AiRoutingTests
{
    [Fact]
    public void RouteFor_FallsDownTheChain()
    {
        var research = new AiRoute(new AiConnectionSettings("r", AiWireProtocol.Google, null, AiAuthMode.ApiKey, "k", null, null, true), "strong");
        var repair = research with { Model = "cheap" };
        var settings = new AiProviderSettings(AiProvider.Routed, null, null, null)
        {
            Routes = new Dictionary<AiFeature, AiRoute> { [AiFeature.ScriptResearch] = research, [AiFeature.ScriptRepair] = repair },
        };

        Assert.Equal("strong", settings.RouteFor(AiFeature.ScriptResearch)!.Model);
        Assert.Equal("cheap", settings.RouteFor(AiFeature.ScriptRepair)!.Model);
        // CPE suggestion has no route of its own, so it takes repair's, not research's.
        Assert.Equal("cheap", settings.RouteFor(AiFeature.CpeSuggestion)!.Model);
    }

    [Theory]
    [InlineData(AiProvider.Anthropic, AiWireProtocol.Anthropic, "claude-sonnet-4-5", true)]
    [InlineData(AiProvider.OpenAI, AiWireProtocol.OpenAiResponses, "gpt-5", true)]
    [InlineData(AiProvider.Ollama, AiWireProtocol.Ollama, "", false)]
    public void TheOriginalProviders_BecomeFixedRoutesWithTheirOldDefaults(AiProvider provider, AiWireProtocol protocol, string model, bool hosted)
    {
        var route = AiUpgradePathResearchClient.LegacyRoute(new AiProviderSettings(provider, "key", "http://ollama:11434", null));

        Assert.Equal(protocol, route.Connection.Protocol);
        Assert.Equal(model, route.Model);
        Assert.Equal(hosted, route.Connection.UseHostedWebSearch);
    }

    [Fact]
    public async Task Resolver_RoutedModeIsUnconfiguredWithoutAResearchRoute()
    {
        var settings = AiAgentSettings.Create(AiProvider.Routed, null, null, null, isEnabled: true);
        var connection = AiConnection.Create("Gemini", "google", AiWireProtocol.Google, null, AiAuthMode.ApiKey, "k", null, null, true);
        var repository = new Mock<IAiAgentSettingsRepository>();
        repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var withoutResearch = await TestAiResolver.Over(repository.Object, [connection],
            [AiFeatureRoute.Create(AiFeature.CpeSuggestion, connection.Id, "gemini-flash")]).ResolveAsync(CancellationToken.None);
        var withResearch = await TestAiResolver.Over(repository.Object, [connection],
            [AiFeatureRoute.Create(AiFeature.ScriptResearch, connection.Id, "gemini-pro")]).ResolveAsync(CancellationToken.None);

        Assert.Null(withoutResearch);
        Assert.Equal("gemini-pro", withResearch!.RouteFor(AiFeature.CpeSuggestion)!.Model);
    }

    [Fact]
    public async Task Resolver_KeepsTheLegacyOllamaSearchKeyWorking()
    {
        var repository = new Mock<IAiAgentSettingsRepository>();
        repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AiAgentSettings.Create(AiProvider.Ollama, null, "http://ollama:11434", "llama3", isEnabled: true));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OLLAMA_WEB_API_KEY"] = "legacy" }).Build();

        var resolved = await TestAiResolver.Over(repository.Object, configuration: configuration).ResolveAsync(CancellationToken.None);

        Assert.Equal(WebSearchBackend.OllamaWeb, resolved!.WebSearch.Backend);
        Assert.Equal("legacy", resolved.WebSearch.ApiKey);
    }
}

public class AiConnectionEntityTests
{
    [Fact]
    public void GoogleCloudAuth_StoresNoKey_AndNeedsAProjectAndLocation()
    {
        var connection = AiConnection.Create("Vertex", "google-vertex", AiWireProtocol.Google, null, AiAuthMode.GoogleCloud, "ignored", "corp-tools", "global", true);

        Assert.Null(connection.ApiKey);
        Assert.Throws<DomainException>(() =>
            AiConnection.Create("Vertex", null, AiWireProtocol.Google, null, AiAuthMode.GoogleCloud, null, null, null, true));
    }

    [Fact]
    public void ABlankKeyOnUpdate_KeepsTheStoredOne()
    {
        var connection = AiConnection.Create("Groq", "groq", AiWireProtocol.OpenAiChatCompletions, "https://api.groq.com/openai/v1", AiAuthMode.ApiKey, "gsk_1", null, null, false);

        connection.Update("Groq", "groq", AiWireProtocol.OpenAiChatCompletions, "https://api.groq.com/openai/v1", AiAuthMode.ApiKey, "  ", null, null, false);

        Assert.Equal("gsk_1", connection.ApiKey);
    }

    [Fact]
    public void HostedSearch_IsDroppedForAProtocolWithoutOne()
    {
        var connection = AiConnection.Create("LiteLLM", null, AiWireProtocol.OpenAiChatCompletions, "https://litellm.internal/v1", AiAuthMode.None, null, null, null, useHostedWebSearch: true);

        Assert.False(connection.UseHostedWebSearch);
    }

    [Fact]
    public void WebSearch_NeedsTheBackendsCredential()
    {
        var settings = AiAgentSettings.Create(AiProvider.Routed, null, null, null, isEnabled: true);

        Assert.Throws<DomainException>(() => settings.UpdateWebSearch(WebSearchBackend.Tavily, null, false, null));
        Assert.Throws<DomainException>(() => settings.UpdateWebSearch(WebSearchBackend.SearXng, null, false, null));
        settings.UpdateWebSearch(WebSearchBackend.SearXng, null, false, "https://searx.internal/");
        Assert.Equal("https://searx.internal", settings.WebSearchBaseUrl);
    }
}

public class SafeWebFetcherTests
{
    [Theory]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("10.0.0.5", false)]
    [InlineData("172.20.1.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("100.64.0.1", false)] // CGNAT
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)] // unique local
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:169.254.169.254", false)] // IPv4-mapped metadata
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void IsPublic(string address, bool expected) =>
        Assert.Equal(expected, SafeWebFetcher.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://kintsugi.kintsugi.svc.cluster.local/")]
    [InlineData("file:///etc/passwd")]
    public async Task RefusesNonPublicTargets_WithoutConnecting(string url)
    {
        var result = await new SafeWebFetcher().FetchTextAsync(url, CancellationToken.None);

        Assert.StartsWith("Refused", result);
    }

    [Fact]
    public void HtmlToText_DropsScriptsAndKeepsLinks()
    {
        var text = SafeWebFetcher.HtmlToText("<html><script>evil()</script><p>Latest: <a href=\"https://x/140\">140.0</a></p></html>");

        Assert.DoesNotContain("evil", text);
        Assert.Contains("140.0 (https://x/140)", text);
    }
}

public class ModelsDevCatalogTests
{
    [Fact]
    public void Map_DerivesTheProtocolPerModel_AndDropsWhatNoAdapterReaches()
    {
        var raw = JsonNode.Parse("""
            {
              "google-vertex": {
                "id": "google-vertex", "name": "Vertex", "npm": "@ai-sdk/google-vertex",
                "env": ["GOOGLE_VERTEX_PROJECT"],
                "models": {
                  "gemini-2.5-pro": { "id": "gemini-2.5-pro", "name": "Gemini 2.5 Pro", "tool_call": true, "reasoning": true, "limit": { "context": 1048576, "output": 65536 } },
                  "claude-sonnet-4-5@20250929": { "id": "claude-sonnet-4-5@20250929", "name": "Claude Sonnet 4.5", "tool_call": true, "limit": { "context": 200000, "output": 64000 }, "provider": { "npm": "@ai-sdk/google-vertex/anthropic" } }
                }
              },
              "amazon-bedrock": {
                "id": "amazon-bedrock", "name": "Bedrock", "npm": "@ai-sdk/amazon-bedrock", "env": ["AWS_REGION"],
                "models": { "x": { "id": "x", "name": "X", "tool_call": true, "limit": { "context": 1, "output": 1 } } }
              },
              "groq": {
                "id": "groq", "name": "Groq", "npm": "@ai-sdk/groq", "env": ["GROQ_API_KEY"],
                "models": { "llama": { "id": "llama", "name": "Llama", "tool_call": true, "limit": { "context": 1, "output": 1 } } }
              }
            }
            """)!.AsObject();

        var catalog = ModelsDevCatalog.Map(raw, DateTimeOffset.UtcNow);

        Assert.DoesNotContain(catalog.Providers, p => p.Id == "amazon-bedrock");
        var vertex = catalog.Providers.Single(p => p.Id == "google-vertex");
        Assert.True(vertex.UsesGoogleCloud);
        Assert.False(vertex.RequiresApiKey);
        Assert.Equal(AiWireProtocol.Google, vertex.Models.Single(m => m.Id == "gemini-2.5-pro").Protocol);
        Assert.Equal(AiWireProtocol.Anthropic, vertex.Models.Single(m => m.Id.StartsWith("claude")).Protocol);
        var groq = catalog.Providers.Single(p => p.Id == "groq");
        Assert.Equal(AiWireProtocol.OpenAiChatCompletions, groq.Protocol);
        Assert.Equal("https://api.groq.com/openai/v1", groq.DefaultBaseUrl);
        Assert.True(groq.RequiresApiKey);
    }
}
