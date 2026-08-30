using FluentValidation;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.AiSettings.Commands.UpdateAiAgentSettings;

public class UpdateAiAgentSettingsCommandValidator : AbstractValidator<UpdateAiAgentSettingsCommand>
{
    public UpdateAiAgentSettingsCommandValidator()
    {
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.ApiKey).MaximumLength(512);
        RuleFor(x => x.Model).MaximumLength(128);

        RuleFor(x => x.BaseUrl)
            .NotEmpty()
            .WithMessage("A base URL is required when connecting to a local Ollama endpoint.")
            .When(x => x.Provider == AiProvider.Ollama);

        // GooseCli's BaseUrl is the base URL of a `goose serve` instance; unlike Ollama's, it's
        // optional (blank falls back to Goose's own default local address), so it only needs to
        // be a valid absolute URL when actually provided.
        RuleFor(x => x.BaseUrl)
            .MaximumLength(512)
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .WithMessage("Base URL must be a valid absolute URL.")
            .When(x => !string.IsNullOrWhiteSpace(x.BaseUrl) && (x.Provider == AiProvider.Ollama || x.Provider == AiProvider.GooseCli));
    }
}
