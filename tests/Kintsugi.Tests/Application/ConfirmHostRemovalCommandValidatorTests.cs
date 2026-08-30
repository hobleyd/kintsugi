using FluentValidation.TestHelper;
using Kintsugi.Application.Hosts.Commands.ConfirmHostRemoval;

namespace Kintsugi.Tests.Application;

public class ConfirmHostRemovalCommandValidatorTests
{
    private readonly ConfirmHostRemovalCommandValidator _validator = new();

    [Fact]
    public void ValidCommand_PassesValidation()
    {
        var result = _validator.TestValidate(new ConfirmHostRemovalCommand("SERIAL-1"));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void SerialNumber_Empty_IsRejected()
    {
        var result = _validator.TestValidate(new ConfirmHostRemovalCommand(""));

        result.ShouldHaveValidationErrorFor(c => c.SerialNumber);
    }

    [Fact]
    public void SerialNumber_LongerThan128Characters_IsRejected()
    {
        var result = _validator.TestValidate(new ConfirmHostRemovalCommand(new string('A', 129)));

        result.ShouldHaveValidationErrorFor(c => c.SerialNumber);
    }
}
