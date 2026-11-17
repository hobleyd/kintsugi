using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Baselines.Commands.CreateBaseline;

public class CreateBaselineCommandValidator : AbstractValidator<CreateBaselineCommand>
{
    public CreateBaselineCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Baseline.MaxNameLength);
        RuleFor(c => c.Items).NotNull().SetValidator(new BaselineItemsValidator());
    }
}
