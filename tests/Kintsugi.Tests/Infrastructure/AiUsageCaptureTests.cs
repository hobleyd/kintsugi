using Kintsugi.Application.AiSettings;
using Kintsugi.Domain.Enums;
using Kintsugi.Infrastructure.Ai.Engine;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// Every adapter reads the tokens its provider says a call used, and the engine hands them to
/// whoever opened an <see cref="AiUsageScope"/> — which is how the budget learns what was spent
/// without the engine knowing why it was asked.
/// </summary>
public class AiUsageCaptureTests
{
    private static AiConnectionSettings Connection(AiWireProtocol protocol, string? baseUrl = null) =>
        new("test", protocol, baseUrl, AiAuthMode.ApiKey, "k", null, null, false);

    private static async Task<IReadOnlyList<AiUsageEntry>> Capture(RoutingHandler handler, AiRoute route)
    {
        using var scope = AiUsageScope.Begin();
        await TestAiEngine.Over(handler).CompleteAsync(route, "hi", null, CancellationToken.None);
        return scope.Entries;
    }

    [Fact]
    public async Task Anthropic_CountsCacheTokensAsInput()
    {
        var handler = new RoutingHandler().OnJson(_ => true, _ => new
        {
            content = new[] { new { type = "text", text = "ok" } },
            usage = new { input_tokens = 100, cache_creation_input_tokens = 20, cache_read_input_tokens = 5, output_tokens = 40 },
        });

        var entry = Assert.Single(await Capture(handler, new AiRoute(Connection(AiWireProtocol.Anthropic), "claude-sonnet-5-5")));

        Assert.Equal(125, entry.InputTokens);
        Assert.Equal(40, entry.OutputTokens);
        Assert.Equal("claude-sonnet-5-5", entry.Model);
        Assert.False(entry.Free);
    }

    [Fact]
    public async Task OpenAiChatCompletions_ReadsPromptAndCompletionTokens()
    {
        var handler = new RoutingHandler().OnJson(_ => true, _ => new
        {
            choices = new[] { new { message = new { role = "assistant", content = "ok" } } },
            usage = new { prompt_tokens = 321, completion_tokens = 45 },
        });

        var entry = Assert.Single(await Capture(handler, new AiRoute(Connection(AiWireProtocol.OpenAiChatCompletions, "https://example.test/v1"), "m")));

        Assert.Equal((321, 45), (entry.InputTokens, entry.OutputTokens));
    }

    [Fact]
    public async Task OpenAiResponses_ReadsInputAndOutputTokens()
    {
        var handler = new RoutingHandler().OnJson(_ => true, _ => new
        {
            output = new object[] { new { type = "message", content = new[] { new { type = "output_text", text = "ok" } } } },
            usage = new { input_tokens = 900, output_tokens = 70 },
        });

        var entry = Assert.Single(await Capture(handler, new AiRoute(Connection(AiWireProtocol.OpenAiResponses), "gpt-5.4-mini")));

        Assert.Equal((900, 70), (entry.InputTokens, entry.OutputTokens));
    }

    [Fact]
    public async Task Google_CountsThinkingTokensAsOutput()
    {
        var handler = new RoutingHandler().OnJson(_ => true, _ => new
        {
            candidates = new[] { new { content = new { role = "model", parts = new object[] { new { text = "ok" } } } } },
            usageMetadata = new { promptTokenCount = 50, candidatesTokenCount = 10, thoughtsTokenCount = 200 },
        });

        var entry = Assert.Single(await Capture(handler, new AiRoute(Connection(AiWireProtocol.Google), "gemini-2.5-pro")));

        Assert.Equal((50, 210), (entry.InputTokens, entry.OutputTokens));
    }

    [Fact]
    public async Task Ollama_ReportsTokensAndIsMarkedFree()
    {
        var handler = new RoutingHandler().OnJson(_ => true, _ => new
        {
            message = new { role = "assistant", content = "ok" },
            prompt_eval_count = 640,
            eval_count = 80,
        });

        var entry = Assert.Single(await Capture(handler, new AiRoute(Connection(AiWireProtocol.Ollama, "http://localhost:11434"), "llama3")));

        Assert.Equal((640, 80), (entry.InputTokens, entry.OutputTokens));
        Assert.True(entry.Free, "a local model costs nothing, which is not the same as having no known price");
    }

    [Fact]
    public async Task AProviderThatReportsNothingLeavesNoEntry()
    {
        var handler = new RoutingHandler().OnJson(_ => true, _ => new { content = new[] { new { type = "text", text = "ok" } } });

        Assert.Empty(await Capture(handler, new AiRoute(Connection(AiWireProtocol.Anthropic), "claude-sonnet-5-5")));
    }

    [Fact]
    public async Task ACallOutsideAnyScopeIsSimplyNotRecorded()
    {
        var handler = new RoutingHandler().OnJson(_ => true, _ => new
        {
            content = new[] { new { type = "text", text = "ok" } },
            usage = new { input_tokens = 1, output_tokens = 1 },
        });

        var text = await TestAiEngine.Over(handler).CompleteAsync(
            new AiRoute(Connection(AiWireProtocol.Anthropic), "claude-sonnet-5-5"), "hi", null, CancellationToken.None);

        Assert.Equal("ok", text);
    }
}
