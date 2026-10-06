using FluentValidation;

namespace Kintsugi.Application.GitHub.Commands.CompleteGitHubAppManifest;

public class CompleteGitHubAppManifestCommandValidator : AbstractValidator<CompleteGitHubAppManifestCommand>
{
    public CompleteGitHubAppManifestCommandValidator()
    {
        // The code is interpolated into the conversion URL's path, so anything beyond the
        // characters GitHub issues it in is refused rather than escaped and sent.
        RuleFor(c => c.Code)
            .NotEmpty()
            .MaximumLength(100)
            .Matches("^[A-Za-z0-9_-]+$")
            .WithMessage("GitHub's manifest code was missing or malformed. Start the GitHub App setup again.");
    }
}
