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

    /// <summary>
    /// The file name is joined onto the platform directory by AgentPackageFileStorage, and a name
    /// carrying a path wrote outside it — verified against a live deployment before this rule
    /// existed. Dot entries are listed separately because the character class alone admits them.
    /// </summary>
    [Theory]
    [InlineData("../kintsugi-traversal-proof.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("/data/agent-ca-private/ca.key")]
    [InlineData("sub/dir.tar.gz")]
    [InlineData("back\\slash.tar.gz")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden.tar.gz")]
    [InlineData("has space.tar.gz")]
    [InlineData("new\nline.tar.gz")]
    public void FileName_ThatIsNotABareName_IsRejected(string fileName)
    {
        var command = ValidCommand() with { FileName = fileName };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.FileName);
    }

    /// <summary>What CI actually names the archives, and the Windows one — both must keep passing.</summary>
    [Theory]
    [InlineData("kintsugi-agent-linux-v0.13.0.tar.gz")]
    [InlineData("kintsugi-agent-windows-installer.tar.gz")]
    [InlineData("kintsugi-agent-macos-0.2.0.tar.gz")]
    [InlineData("payload_1.tar.gz")]
    public void FileName_ThatIsABareName_IsAccepted(string fileName)
    {
        var command = ValidCommand() with { FileName = fileName };

        _validator.TestValidate(command).ShouldNotHaveValidationErrorFor(x => x.FileName);
    }

    [Fact]
    public void Content_Null_IsRejected()
    {
        var command = ValidCommand() with { Content = null! };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Content);
    }
}
