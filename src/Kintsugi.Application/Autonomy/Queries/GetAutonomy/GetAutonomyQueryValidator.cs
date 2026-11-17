using FluentValidation;

namespace Kintsugi.Application.Autonomy.Queries.GetAutonomy;

public class GetAutonomyQueryValidator : AbstractValidator<GetAutonomyQuery>
{
    public GetAutonomyQueryValidator()
    {
        RuleFor(x => x.Period).Must(p => p is "7d" or "30d" or "month").WithMessage("Period must be 7d, 30d or month.");
    }
}
