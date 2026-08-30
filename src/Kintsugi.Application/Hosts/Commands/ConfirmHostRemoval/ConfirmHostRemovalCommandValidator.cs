using FluentValidation;

namespace Kintsugi.Application.Hosts.Commands.ConfirmHostRemoval;

public class ConfirmHostRemovalCommandValidator : AbstractValidator<ConfirmHostRemovalCommand>
{
    public ConfirmHostRemovalCommandValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128);
    }
}
