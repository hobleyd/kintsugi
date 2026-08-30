using FluentValidation.TestHelper;
using Kintsugi.Application.Hosts.Commands.CreateHost;

namespace Kintsugi.Tests.Application;

public class CreateHostCommandValidatorTests
{
    private readonly CreateHostCommandValidator _validator = new();

    [Fact]
    public void ValidCommand_PassesValidation()
    {
        var result = _validator.TestValidate(
            new CreateHostCommand("host-1", "SERIAL-1", OperatingSystem: "macOS 15.0", IpAddress: "10.0.0.1"));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Hostname_Empty_IsRejected()
    {
        var result = _validator.TestValidate(new CreateHostCommand("", "SERIAL-1"));

        result.ShouldHaveValidationErrorFor(c => c.Hostname);
    }

    [Fact]
    public void SerialNumber_Empty_IsRejected()
    {
        var result = _validator.TestValidate(new CreateHostCommand("host-1", ""));

        result.ShouldHaveValidationErrorFor(c => c.SerialNumber);
    }

    [Fact]
    public void SerialNumber_LongerThan128Characters_IsRejected()
    {
        var result = _validator.TestValidate(new CreateHostCommand("host-1", new string('A', 129)));

        result.ShouldHaveValidationErrorFor(c => c.SerialNumber);
    }

    [Fact]
    public void OperatingSystemLatestVersion_LongerThan64Characters_IsRejected()
    {
        var result = _validator.TestValidate(new CreateHostCommand("host-1", "SERIAL-1", OperatingSystemLatestVersion: new string('1', 65)));

        result.ShouldHaveValidationErrorFor(c => c.OperatingSystemLatestVersion);
    }

    [Fact]
    public void OperatingSystemLatestVersion_NotProvided_IsAccepted()
    {
        var result = _validator.TestValidate(new CreateHostCommand("host-1", "SERIAL-1"));

        result.ShouldNotHaveValidationErrorFor(c => c.OperatingSystemLatestVersion);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    public void CheckInMinute_OutOfRange_IsRejected(int minute)
    {
        var result = _validator.TestValidate(new CreateHostCommand("host-1", "SERIAL-1", CheckInMinute: minute));

        result.ShouldHaveValidationErrorFor(c => c.CheckInMinute);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    public void CheckInMinute_WithinRange_IsAccepted(int minute)
    {
        var result = _validator.TestValidate(new CreateHostCommand("host-1", "SERIAL-1", CheckInMinute: minute));

        result.ShouldNotHaveValidationErrorFor(c => c.CheckInMinute);
    }
}
