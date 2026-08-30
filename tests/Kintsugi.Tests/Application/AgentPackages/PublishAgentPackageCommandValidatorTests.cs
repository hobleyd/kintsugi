using FluentValidation.TestHelper;
using Kintsugi.Application.AgentPackages.Commands.PublishAgentPackage;

namespace Kintsugi.Tests.Application.AgentPackages;

public class PublishAgentPackageCommandValidatorTests
{
    private readonly PublishAgentPackageCommandValidator _validator = new();

    private static PublishAgentPackageCommand ValidCommand() =>
        new("macos", "0.2.0", "Fixes self-update.", "kintsugi-agent-macos-0.2.0.tar.gz", new MemoryStream(new byte[] { 1, 2, 3 }));

    [Fact]
    public void ValidCommand_PassesValidation()
    {
        var result = _validator.TestValidate(ValidCommand());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Platform_Empty_IsRejected()
    {
        var command = ValidCommand() with { Platform = "" };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Platform);
    }

    [Theory]
    [InlineData("mac os")]
    [InlineData("macos/../etc")]
    [InlineData("macos!")]
    public void Platform_WithDisallowedCharacters_IsRejected(string platform)
    {
        var command = ValidCommand() with { Platform = platform };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Platform);
    }

    [Fact]
    public void Version_Empty_IsRejected()
    {
        var command = ValidCommand() with { Version = "" };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Version);
    }

    [Fact]
    public void FileName_Empty_IsRejected()
    {
        var command = ValidCommand() with { FileName = "" };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.FileName);
    }

    [Fact]
    public void Content_Null_IsRejected()
    {
        var command = ValidCommand() with { Content = null! };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Content);
    }
}
