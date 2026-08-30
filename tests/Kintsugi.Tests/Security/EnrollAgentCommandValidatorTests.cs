using FluentValidation.TestHelper;
using Kintsugi.Application.Hosts.Commands.EnrollAgent;

namespace Kintsugi.Tests.Security;

public class EnrollAgentCommandValidatorTests
{
    private readonly EnrollAgentCommandValidator _validator = new();

    private static EnrollAgentCommand ValidCommand(string? serialNumber = "SERIAL-123", string? token = "a-token", string? csrPem = null) =>
        new(serialNumber!, token!, csrPem ?? "-----BEGIN CERTIFICATE REQUEST-----\nMII...\n-----END CERTIFICATE REQUEST-----");

    [Fact]
    public void ValidCommand_PassesValidation()
    {
        var result = _validator.TestValidate(ValidCommand());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void SerialNumber_Empty_IsRejected(string serialNumber)
    {
        var result = _validator.TestValidate(ValidCommand(serialNumber: serialNumber));

        result.ShouldHaveValidationErrorFor(c => c.SerialNumber);
    }

    [Fact]
    public void SerialNumber_LongerThan128Characters_IsRejected()
    {
        var result = _validator.TestValidate(ValidCommand(serialNumber: new string('A', 129)));

        result.ShouldHaveValidationErrorFor(c => c.SerialNumber);
    }

    // This becomes the issued certificate's Subject CN via plain string interpolation (see
    // CaService) — a character set outside [A-Za-z0-9._-] would risk injecting extra RDNs.
    [Theory]
    [InlineData("abc,CN=evil")]
    [InlineData("abc+CN=evil")]
    [InlineData("abc/../etc")]
    [InlineData("abc CN=evil")]
    public void SerialNumber_WithCharactersUnsafeForADistinguishedName_IsRejected(string serialNumber)
    {
        var result = _validator.TestValidate(ValidCommand(serialNumber: serialNumber));

        result.ShouldHaveValidationErrorFor(c => c.SerialNumber);
    }

    [Theory]
    [InlineData("SERIAL-123")]
    [InlineData("C02.ABC123_xyz")]
    public void SerialNumber_WithOnlySafeCharacters_IsAccepted(string serialNumber)
    {
        var result = _validator.TestValidate(ValidCommand(serialNumber: serialNumber));

        result.ShouldNotHaveValidationErrorFor(c => c.SerialNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EnrollmentToken_Empty_IsRejected(string token)
    {
        var result = _validator.TestValidate(ValidCommand(token: token));

        result.ShouldHaveValidationErrorFor(c => c.EnrollmentToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just some random text")]
    [InlineData("-----BEGIN CERTIFICATE-----\nMII...\n-----END CERTIFICATE-----")]
    public void CsrPem_NotAPemEncodedCertificateRequest_IsRejected(string csrPem)
    {
        var result = _validator.TestValidate(ValidCommand(csrPem: csrPem));

        result.ShouldHaveValidationErrorFor(c => c.CsrPem);
    }
}
