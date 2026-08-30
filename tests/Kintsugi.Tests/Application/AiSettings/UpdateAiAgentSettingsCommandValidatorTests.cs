using FluentValidation.TestHelper;
using Kintsugi.Application.AiSettings.Commands.UpdateAiAgentSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.AiSettings;

public class UpdateAiAgentSettingsCommandValidatorTests
{
    private readonly UpdateAiAgentSettingsCommandValidator _validator = new();

    [Fact]
    public void Anthropic_WithNoBaseUrl_IsValid()
    {
        var result = _validator.TestValidate(new UpdateAiAgentSettingsCommand(AiProvider.Anthropic, "sk-123", null, null, true));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Ollama_WithNoBaseUrl_IsRejected()
    {
        var result = _validator.TestValidate(new UpdateAiAgentSettingsCommand(AiProvider.Ollama, null, null, null, true));

        result.ShouldHaveValidationErrorFor(c => c.BaseUrl);
    }

    [Fact]
    public void Ollama_WithAValidBaseUrl_IsAccepted()
    {
        var result = _validator.TestValidate(new UpdateAiAgentSettingsCommand(AiProvider.Ollama, null, "http://localhost:11434", null, true));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Ollama_WithANonUrlBaseUrl_IsRejected()
    {
        var result = _validator.TestValidate(new UpdateAiAgentSettingsCommand(AiProvider.Ollama, null, "not a url", null, true));

        result.ShouldHaveValidationErrorFor(c => c.BaseUrl);
    }

    [Fact]
    public void GooseCli_WithNoBaseUrl_IsAccepted_SinceItFallsBackToGoosesOwnDefault()
    {
        var result = _validator.TestValidate(new UpdateAiAgentSettingsCommand(AiProvider.GooseCli, null, null, null, true));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void GooseCli_WithANonUrlBaseUrl_IsRejected()
    {
        var result = _validator.TestValidate(new UpdateAiAgentSettingsCommand(AiProvider.GooseCli, null, "not a url", null, true));

        result.ShouldHaveValidationErrorFor(c => c.BaseUrl);
    }

    [Fact]
    public void ApiKey_LongerThan512Characters_IsRejected()
    {
        var result = _validator.TestValidate(new UpdateAiAgentSettingsCommand(AiProvider.Anthropic, new string('a', 513), null, null, true));

        result.ShouldHaveValidationErrorFor(c => c.ApiKey);
    }
}
