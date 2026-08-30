using FluentValidation;

namespace Kintsugi.Application.AgentPackages.Commands.PublishAgentPackage;

public class PublishAgentPackageCommandValidator : AbstractValidator<PublishAgentPackageCommand>
{
    public PublishAgentPackageCommandValidator()
    {
        RuleFor(x => x.Platform)
            .NotEmpty()
            .MaximumLength(32)
            .Matches("^[a-zA-Z0-9-]+$").WithMessage("Platform may only contain letters, numbers, and hyphens.");

        RuleFor(x => x.Version).NotEmpty().MaximumLength(64);
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.ReleaseNotes).MaximumLength(2000);
        RuleFor(x => x.Content).NotNull();
    }
}
