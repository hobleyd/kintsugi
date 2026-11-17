using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.AiSettings.Commands.UpdateAiBudget;

public class UpdateAiBudgetCommandValidator : AbstractValidator<UpdateAiBudgetCommand>
{
    public UpdateAiBudgetCommandValidator()
    {
        // The same rule the entity enforces, so it is reported as a 400 naming the field rather
        // than a domain error — see AiBudgetSettings.BudgetDecimalPlaces for why sub-cent amounts
        // are refused instead of rounded.
        RuleFor(x => x.BudgetAmount).Must(a => AiBudgetSettings.IsStorableBudget(a!.Value)).When(x => x.BudgetAmount is not null)
            .WithMessage("The monthly budget must be at least 0.01 with no more than two decimal places, or left empty for no limit.");
        RuleFor(x => x.BudgetAmount).LessThan(10_000_000m).When(x => x.BudgetAmount is not null);
        RuleFor(x => x.BudgetCurrency).Must(c => string.Equals(c, "USD", StringComparison.OrdinalIgnoreCase) || string.Equals(c, "AUD", StringComparison.OrdinalIgnoreCase))
            .WithMessage("The budget currency must be USD or AUD.");
        RuleFor(x => x.UsdToAudRate).GreaterThan(0m).LessThan(1000m)
            .WithMessage("The exchange rate must be more than zero.");
        RuleFor(x => x.WarnPercent).InclusiveBetween(1, 99)
            .WithMessage("The warning level must be between 1 and 99 percent.");
    }
}
