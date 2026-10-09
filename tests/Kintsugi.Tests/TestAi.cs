using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Infrastructure.Ai;
using Kintsugi.Infrastructure.Ai.Engine;

namespace Kintsugi.Tests;

/// <summary>The real <see cref="AiProviderSettingsResolver"/>, over a settings repository a test
/// controls and no connections or routes unless given.</summary>
public static class TestAiResolver
{
    public static IAiProviderSettingsResolver Over(
        IAiAgentSettingsRepository settings,
        IReadOnlyList<AiConnection>? connections = null,
        IReadOnlyList<AiFeatureRoute>? routes = null,
        IConfiguration? configuration = null)
    {
        var connectionRepository = new Mock<IAiConnectionRepository>();
        connectionRepository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(connections ?? []);
        var routeRepository = new Mock<IAiFeatureRouteRepository>();
        routeRepository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(routes ?? []);
        return new AiProviderSettingsResolver(
            settings, connectionRepository.Object, routeRepository.Object, configuration ?? new ConfigurationBuilder().Build());
    }
}

/// <summary>The real <see cref="AiEngine"/> with every adapter, sending through
/// <paramref name="handler"/>. Google Cloud tokens come from the same handler, so a test can answer
/// the metadata server too.</summary>
public static class TestAiEngine
{
    public static AiEngine Over(HttpMessageHandler handler, SafeWebFetcher? fetcher = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        var http = new AiHttp(new HttpClient(handler, disposeHandler: false), new GoogleCloudAccessTokenProvider(factory.Object));
        IAiProtocolAdapter[] adapters =
        [
            new OpenAiChatCompletionsAdapter(http),
            new OpenAiResponsesAdapter(http),
            new AnthropicAdapter(http),
            new GoogleAdapter(http),
            new OllamaAdapter(http),
        ];
        return new AiEngine(adapters, new WebSearchClient(new HttpClient(handler, disposeHandler: false)), fetcher ?? new SafeWebFetcher(), NullLogger<AiEngine>.Instance);
    }
}
