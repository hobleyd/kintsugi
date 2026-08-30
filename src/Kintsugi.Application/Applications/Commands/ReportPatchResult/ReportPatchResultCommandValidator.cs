using FluentValidation;

namespace Kintsugi.Application.Applications.Commands.ReportPatchResult;

public class ReportPatchResultCommandValidator : AbstractValidator<ReportPatchResultCommand>
{
    public ReportPatchResultCommandValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128);
        RuleFor(x => x.ApplicationName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.NewVersion).NotEmpty().MaximumLength(64);
    }
}
