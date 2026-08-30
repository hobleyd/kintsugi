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
    }
}
