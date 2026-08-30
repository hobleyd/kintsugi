using FluentValidation;

namespace Kintsugi.Application.Patches.Commands.CreatePatch;

public class CreatePatchCommandValidator : AbstractValidator<CreatePatchCommand>
{
    public CreatePatchCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Vendor).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Version).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Severity).IsInEnum();
    }
}
