using FluentValidation;

namespace Kintsugi.Application.Applications.Commands.RegisterApplications;

public class RegisterApplicationsCommandValidator : AbstractValidator<RegisterApplicationsCommand>
{
    public RegisterApplicationsCommandValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Applications).NotNull();

        RuleForEach(x => x.Applications).ChildRules(app =>
        {
            app.RuleFor(a => a.Name).NotEmpty().MaximumLength(255);
            app.RuleFor(a => a.Version).NotEmpty().MaximumLength(64);
            app.RuleFor(a => a.PackageManager).MaximumLength(255);
            app.RuleFor(a => a.ApplicationIdentifier).MaximumLength(255);
            app.RuleFor(a => a.AvailableVersion).MaximumLength(64);
        });
    }
}
