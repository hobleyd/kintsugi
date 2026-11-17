using FluentValidation;

namespace Kintsugi.Application.Autonomy.Queries.GetAutonomyCauseDevices;

public class GetAutonomyCauseDevicesQueryValidator : AbstractValidator<GetAutonomyCauseDevicesQuery>
{
    public const int MaxTake = 200;

    public GetAutonomyCauseDevicesQueryValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake);
        RuleFor(x => x.Search).MaximumLength(100);
    }
}
