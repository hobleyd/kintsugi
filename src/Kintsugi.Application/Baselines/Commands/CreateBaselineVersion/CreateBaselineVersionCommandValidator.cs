using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Commands.CreateBaselineVersion;

public class CreateBaselineVersionCommandValidator : AbstractValidator<CreateBaselineVersionCommand>
{
    public CreateBaselineVersionCommandValidator()
    {
        RuleFor(c => c.BaselineId).NotEmpty();
        RuleFor(c => c.Items).NotNull().SetValidator(new BaselineItemsValidator());
    }
}
