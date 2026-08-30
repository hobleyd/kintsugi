using FluentValidation.TestHelper;
using Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.PatchingPolicy;

public class UpdatePatchingPolicySettingsCommandValidatorTests
{
    private readonly UpdatePatchingPolicySettingsCommandValidator _validator = new();

    [Fact]
    public void ValidCommand_PassesValidation()
    {
        var result = _validator.TestValidate(new UpdatePatchingPolicySettingsCommand(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void IntervalValue_BelowOne_IsRejected()
    {
        var result = _validator.TestValidate(new UpdatePatchingPolicySettingsCommand(0, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3));

        result.ShouldHaveValidationErrorFor(c => c.IntervalValue);
    }

    [Fact]
    public void DelayValue_BelowOne_IsRejected()
    {
        var result = _validator.TestValidate(new UpdatePatchingPolicySettingsCommand(7, PatchingTimeUnit.Days, 0, PatchingTimeUnit.Days, 3));

        result.ShouldHaveValidationErrorFor(c => c.DelayValue);
    }

    [Fact]
    public void MaxDelayCount_Negative_IsRejected()
    {
        var result = _validator.TestValidate(new UpdatePatchingPolicySettingsCommand(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, -1));

        result.ShouldHaveValidationErrorFor(c => c.MaxDelayCount);
    }

    [Fact]
    public void MaxDelayCount_Zero_IsAccepted()
    {
        var result = _validator.TestValidate(new UpdatePatchingPolicySettingsCommand(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 0));

        result.ShouldNotHaveValidationErrorFor(c => c.MaxDelayCount);
    }
}
