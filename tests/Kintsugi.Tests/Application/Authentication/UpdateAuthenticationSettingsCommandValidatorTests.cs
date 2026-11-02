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

    private static UpdateAuthenticationSettingsCommand WithAdmins(params string[] admins) =>
        new(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true, admins);

    // Surrounding whitespace is trimmed (and trimmed again on save); a blank entry is dropped on save.
    [Theory]
    [InlineData("alice.admin@example.invalid")]
    [InlineData("first.last+tag@sub.example.invalid")]
    [InlineData("  alice.admin@example.invalid  ")]
    [InlineData("")]
    public void AdminEmail_PlainAddress_IsAccepted(string email)
    {
        _validator.TestValidate(WithAdmins(email)).ShouldNotHaveAnyValidationErrors();
    }

    // Each of these would be saved and then never match a sign-in, so the administrator would only
    // find out from "Access denied" — the Outlook-style ones being what #30's paste-a-list can produce.
    // Non-ASCII is refused because ToLowerInvariant folds the Kelvin sign (U+212A) into 'k'.
    [Theory]
    [InlineData("Alice Admin <alice.admin@example.invalid>")]
    [InlineData("<alice.admin@example.invalid>")]
    [InlineData("alice admin@example.invalid")]
    [InlineData("a@example.invalid; b@example.invalid")]
    [InlineData("\"alice\"@example.invalid")]
    [InlineData("\u212Aelvin@example.invalid")]
    [InlineData("josé@example.invalid")]
    [InlineData("alice@admin@example.invalid")]
    [InlineData("@example.invalid")]
    [InlineData("alice.admin@")]
    public void AdminEmail_NotAPlainAddress_IsRejected(string email)
    {
        _validator.TestValidate(WithAdmins(email)).ShouldHaveValidationErrorFor("AdminEmails[0]");
    }

    [Fact]
    public void AdminEmails_UpToTheCap_AreAccepted()
    {
        var admins = Enumerable.Range(0, UpdateAuthenticationSettingsCommandValidator.MaxAdminEmails).Select(i => $"admin{i}@example.invalid").ToArray();

        _validator.TestValidate(WithAdmins(admins)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void AdminEmails_OverTheCap_AreRejected()
    {
        var admins = Enumerable.Range(0, UpdateAuthenticationSettingsCommandValidator.MaxAdminEmails + 1).Select(i => $"admin{i}@example.invalid").ToArray();

        _validator.TestValidate(WithAdmins(admins)).ShouldHaveValidationErrorFor(c => c.AdminEmails!.Count);
    }
}
