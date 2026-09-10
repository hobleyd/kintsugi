using FluentValidation;

namespace Kintsugi.Application.Hosts.Commands.CreateHost;

public class CreateHostCommandValidator : AbstractValidator<CreateHostCommand>
{
    public CreateHostCommandValidator()
    {
        RuleFor(x => x.Hostname).NotEmpty().MaximumLength(255);
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128);
        RuleFor(x => x.CheckInMinute).InclusiveBetween(0, 59);
        RuleFor(x => x.OperatingSystem).MaximumLength(255);
        RuleFor(x => x.IpAddress).MaximumLength(45);
        RuleFor(x => x.OperatingSystemLatestVersion).MaximumLength(64);
        RuleFor(x => x.AgentVersion).MaximumLength(64);
        // Both match the hosts table's columns. A longer value is a bug in the agent rather than
        // a legitimate distribution, and a 400 naming the field beats a truncated CPE version
        // silently matching the wrong CVEs.
        RuleFor(x => x.OperatingSystemId).MaximumLength(64);
        RuleFor(x => x.OperatingSystemVersionId).MaximumLength(64);
    }
}
