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

        // Deliberately generous, and deliberately *above* the agent's own cap.
        //
        // This is the same asymmetry ReportPatchFailureCommandValidator.MaxDetailsLength keeps
        // against each agent's MAX_REPORTED_FAILURE_BYTES, and it matters more here: the package
        // list travels in the same request as the application inventory, so a report rejected for
        // being one package over the line would take the host's applications down with it and
        // leave the Applications screen quietly wrong. A large Debian desktop is around 2000
        // source packages; the agents cap themselves at MAX_REPORTED_PACKAGES and truncate with a
        // warning rather than risk the whole report. Raise this figure before raising theirs,
        // never after.
        RuleFor(x => x.Packages)
            .Must(packages => packages is null || packages.Count <= MaxPackages)
            .WithMessage($"At most {MaxPackages} operating-system packages may be reported in one request.");

        RuleForEach(x => x.Packages).ChildRules(package =>
        {
            package.RuleFor(p => p.Name).NotEmpty().MaximumLength(255);
            // 128 rather than the applications' 64: a distribution version carries a packaging
            // revision and sometimes an epoch, e.g. "2:8.2.3995-1ubuntu2.24".
            package.RuleFor(p => p.Version).NotEmpty().MaximumLength(128);
            package.RuleFor(p => p.Source).NotEmpty().MaximumLength(16);
        });
    }

    /// <summary>The server's ceiling on packages per report. See the rule above for why it sits
    /// above the agents' own cap rather than at it.</summary>
    public const int MaxPackages = 10000;
}
