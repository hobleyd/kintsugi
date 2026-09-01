using FluentValidation;

namespace Kintsugi.Application.GitHub.Commands.UpdateGitHubSettings;

public class UpdateGitHubSettingsCommandValidator : AbstractValidator<UpdateGitHubSettingsCommand>
{
    /// <summary>"owner/repo" and nothing else — not a URL, not a bare name. Every GitHub API URL is
    /// built by interpolating this straight in, so a full https:// URL pasted from a browser would
    /// produce nonsense that 404s with no hint as to why.</summary>
    private const string RepositoryPattern = @"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$";

    private const string RepositoryMessage =
        "Enter a repository as owner/name — for example hobleyd/kintsugi — not a full URL.";

    public UpdateGitHubSettingsCommandValidator()
    {
        RuleFor(c => c.AgentPackageRepository)
            .Matches(RepositoryPattern).WithMessage(RepositoryMessage)
            .When(c => !string.IsNullOrWhiteSpace(c.AgentPackageRepository));

        RuleFor(c => c.ScriptApprovalRepository)
            .Matches(RepositoryPattern).WithMessage(RepositoryMessage)
            .When(c => !string.IsNullOrWhiteSpace(c.ScriptApprovalRepository));
    }
}
