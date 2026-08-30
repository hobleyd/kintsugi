using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class AiAgentSettingsTests
{
    [Fact]
    public void Create_ForAnthropic_RequiresAnApiKey()
    {
        Assert.Throws<DomainException>(() => AiAgentSettings.Create(AiProvider.Anthropic, apiKey: null, baseUrl: null, model: null, isEnabled: true));
    }

    [Fact]
    public void Create_ForAnthropic_WithAnApiKey_Succeeds()
    {
        var settings = AiAgentSettings.Create(AiProvider.Anthropic, "sk-123", baseUrl: null, model: "claude-sonnet-5", isEnabled: true);

        Assert.Equal("sk-123", settings.ApiKey);
        Assert.Equal("claude-sonnet-5", settings.Model);
    }

    [Fact]
    public void Create_ForOllama_RequiresABaseUrl()
    {
        Assert.Throws<DomainException>(() => AiAgentSettings.Create(AiProvider.Ollama, apiKey: null, baseUrl: null, model: null, isEnabled: true));
    }

    [Fact]
    public void Create_ForOllama_IgnoresAnyApiKey_SinceALocalEndpointDoesntUseOne()
    {
        var settings = AiAgentSettings.Create(AiProvider.Ollama, apiKey: "irrelevant", baseUrl: "http://localhost:11434", model: null, isEnabled: true);

        Assert.Null(settings.ApiKey);
        Assert.Equal("http://localhost:11434", settings.BaseUrl);
    }

    [Fact]
    public void Create_ForGooseCli_RequiresNeitherAnApiKeyNorABaseUrl()
    {
        var settings = AiAgentSettings.Create(AiProvider.GooseCli, apiKey: null, baseUrl: null, model: null, isEnabled: true);

        Assert.Null(settings.ApiKey);
        Assert.Null(settings.BaseUrl);
    }

    [Fact]
    public void Update_WithABlankApiKey_KeepsTheCurrentlyStoredOne_RatherThanClearingIt()
    {
        // The UI never round-trips the real key back to the browser, so a blank submission means
        // "unchanged", not "remove it".
        var settings = AiAgentSettings.Create(AiProvider.Anthropic, "sk-original", null, null, true);

        settings.Update(AiProvider.Anthropic, apiKey: "", baseUrl: null, model: "new-model", isEnabled: true);

        Assert.Equal("sk-original", settings.ApiKey);
        Assert.Equal("new-model", settings.Model);
    }

    [Fact]
    public void Update_WithANewNonBlankApiKey_ReplacesTheStoredOne()
    {
        var settings = AiAgentSettings.Create(AiProvider.Anthropic, "sk-original", null, null, true);

        settings.Update(AiProvider.Anthropic, apiKey: "sk-new", baseUrl: null, model: null, isEnabled: true);

        Assert.Equal("sk-new", settings.ApiKey);
    }

    [Fact]
    public void Update_SwitchingFromAnthropicToOllama_ClearsTheStoredApiKey()
    {
        var settings = AiAgentSettings.Create(AiProvider.Anthropic, "sk-original", null, null, true);

        settings.Update(AiProvider.Ollama, apiKey: null, baseUrl: "http://localhost:11434", model: null, isEnabled: true);

        Assert.Null(settings.ApiKey);
    }

    [Fact]
    public void Update_WhitespaceOnlyBaseUrlOrModel_IsStoredAsNull()
    {
        var settings = AiAgentSettings.Create(AiProvider.Anthropic, "sk-123", null, null, true);

        settings.Update(AiProvider.Anthropic, "sk-123", baseUrl: "   ", model: "  ", isEnabled: true);

        Assert.Null(settings.BaseUrl);
        Assert.Null(settings.Model);
    }
}
