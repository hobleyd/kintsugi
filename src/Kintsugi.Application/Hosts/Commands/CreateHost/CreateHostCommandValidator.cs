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
        // RestartBecause deliberately has no length rule. It is a display string, and refusing the
        // whole check-in over it took down the one request that keeps a host from going stale: a
        // Linux host months from its last reboot named every pending package, went past the column,
        // and stopped checking in. Host.RecordRestartState cuts it to the column instead, and the
        // agent (os_update::summarise_packages) keeps it well under in the first place.
    }
}
