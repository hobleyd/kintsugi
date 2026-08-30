using FluentValidation.TestHelper;
using Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Authentication;

public class UpdateAuthenticationSettingsCommandValidatorTests
{
    private readonly UpdateAuthenticationSettingsCommandValidator _validator = new();

    [Fact]
    public void GoogleWorkspace_WithClientIdAndSecret_IsValid()
    {
        var result = _validator.TestValidate(new UpdateAuthenticationSettingsCommand(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void MissingClientId_IsRejected()
    {
        var result = _validator.TestValidate(new UpdateAuthenticationSettingsCommand(AuthProvider.GoogleWorkspace, null, "secret", null, null, null, true));

        result.ShouldHaveValidationErrorFor(c => c.ClientId);
    }

    [Fact]
    public void MicrosoftEntra_WithNoTenantId_IsRejected()
    {
        var result = _validator.TestValidate(new UpdateAuthenticationSettingsCommand(AuthProvider.MicrosoftEntra, "client-id", "secret", null, null, null, true));

        result.ShouldHaveValidationErrorFor(c => c.TenantId);
    }

    [Fact]
    public void MicrosoftEntra_WithATenantId_IsAccepted()
    {
        var result = _validator.TestValidate(new UpdateAuthenticationSettingsCommand(AuthProvider.MicrosoftEntra, "client-id", "secret", null, "tenant-id", null, true));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(AuthProvider.GenericOidc)]
    [InlineData(AuthProvider.Clerk)]
    public void OidcProviders_WithNoAuthority_AreRejected(AuthProvider provider)
    {
        var result = _validator.TestValidate(new UpdateAuthenticationSettingsCommand(provider, "client-id", "secret", null, null, null, true));

        result.ShouldHaveValidationErrorFor(c => c.Authority);
    }

    [Theory]
    [InlineData(AuthProvider.GenericOidc)]
    [InlineData(AuthProvider.Clerk)]
    public void OidcProviders_WithANonUrlAuthority_AreRejected(AuthProvider provider)
    {
        var result = _validator.TestValidate(new UpdateAuthenticationSettingsCommand(provider, "client-id", "secret", "not a url", null, null, true));

        result.ShouldHaveValidationErrorFor(c => c.Authority);
    }

    [Theory]
    [InlineData(AuthProvider.GenericOidc)]
    [InlineData(AuthProvider.Clerk)]
    public void OidcProviders_WithAValidAuthority_AreAccepted(AuthProvider provider)
    {
        var result = _validator.TestValidate(new UpdateAuthenticationSettingsCommand(provider, "client-id", "secret", "https://issuer.example.com", null, null, true));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ClientSecret_LongerThan512Characters_IsRejected()
    {
        var result = _validator.TestValidate(new UpdateAuthenticationSettingsCommand(AuthProvider.GoogleWorkspace, "client-id", new string('a', 513), null, null, null, true));

        result.ShouldHaveValidationErrorFor(c => c.ClientSecret);
    }
}
