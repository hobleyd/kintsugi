using FluentValidation;

namespace Kintsugi.Application.AiSettings.Queries.GetOllamaModels;

public class GetOllamaModelsQueryValidator : AbstractValidator<GetOllamaModelsQuery>
{
    public GetOllamaModelsQueryValidator()
    {
        RuleFor(x => x.BaseUrl)
            .NotEmpty()
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .WithMessage("Base URL must be a valid absolute http(s) URL.");
    }
}
