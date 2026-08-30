using FluentValidation;

namespace Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;

public class UpdatePatchingPolicySettingsCommandValidator : AbstractValidator<UpdatePatchingPolicySettingsCommand>
{
    public UpdatePatchingPolicySettingsCommandValidator()
    {
        RuleFor(x => x.IntervalValue).GreaterThanOrEqualTo(1);
        RuleFor(x => x.IntervalUnit).IsInEnum();
        RuleFor(x => x.DelayValue).GreaterThanOrEqualTo(1);
        RuleFor(x => x.DelayUnit).IsInEnum();
        RuleFor(x => x.MaxDelayCount).GreaterThanOrEqualTo(0);
    }
}
