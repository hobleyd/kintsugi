using FluentValidation.TestHelper;
using Kintsugi.Application.Patches.Commands.CreatePatch;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Patches;

public class CreatePatchCommandValidatorTests
{
    private readonly CreatePatchCommandValidator _validator = new();

    private static CreatePatchCommand ValidCommand() =>
        new("Security Update", "Apple", "15.1", PatchSeverity.Critical, DateTimeOffset.UtcNow, "Description");

    [Fact]
    public void ValidCommand_PassesValidation()
    {
        var result = _validator.TestValidate(ValidCommand());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Name_Empty_IsRejected()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = "" });

        result.ShouldHaveValidationErrorFor(c => c.Name);
    }

    [Fact]
    public void Vendor_Empty_IsRejected()
    {
        var result = _validator.TestValidate(ValidCommand() with { Vendor = "" });

        result.ShouldHaveValidationErrorFor(c => c.Vendor);
    }

    [Fact]
    public void Version_Empty_IsRejected()
    {
        var result = _validator.TestValidate(ValidCommand() with { Version = "" });

        result.ShouldHaveValidationErrorFor(c => c.Version);
    }

    [Fact]
    public void Version_LongerThan64Characters_IsRejected()
    {
        var result = _validator.TestValidate(ValidCommand() with { Version = new string('1', 65) });

        result.ShouldHaveValidationErrorFor(c => c.Version);
    }
}
