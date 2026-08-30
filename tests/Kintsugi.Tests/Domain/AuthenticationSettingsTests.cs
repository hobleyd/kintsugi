using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class AuthenticationSettingsTests
{
    [Fact]
    public void Create_WithNoClientId_Throws()
    {
        Assert.Throws<DomainException>(() =>
            AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, clientId: null, "secret", null, null, null, true));
    }

    [Fact]
    public void Create_WithNoClientSecret_Throws()
    {
        Assert.Throws<DomainException>(() =>
            AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", clientSecret: null, null, null, null, true));
    }

    [Fact]
    public void Create_ForGoogleWorkspace_HostedDomainIsOptional()
    {
        var settings = AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true);

        Assert.Null(settings.HostedDomain);
        Assert.Equal("https://accounts.google.com", settings.ResolveAuthority());
    }

    [Fact]
    public void Create_ForGoogleWorkspace_WithHostedDomain_StoresIt()
    {
        var settings = AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, "example.com", true);

        Assert.Equal("example.com", settings.HostedDomain);
    }

    [Fact]
    public void Create_ForMicrosoftEntra_RequiresATenantId()
    {
        Assert.Throws<DomainException>(() =>
            AuthenticationSettings.Create(AuthProvider.MicrosoftEntra, "client-id", "secret", null, tenantId: null, null, true));
    }

    [Fact]
    public void Create_ForMicrosoftEntra_ResolvesAuthorityFromTenantId()
    {
        var settings = AuthenticationSettings.Create(AuthProvider.MicrosoftEntra, "client-id", "secret", null, "contoso-tenant", null, true);

        Assert.Equal("https://login.microsoftonline.com/contoso-tenant/v2.0", settings.ResolveAuthority());
    }

    [Theory]
    [InlineData(AuthProvider.GenericOidc)]
    [InlineData(AuthProvider.Clerk)]
    public void Create_ForOidcProviders_RequiresAnAuthority(AuthProvider provider)
    {
        Assert.Throws<DomainException>(() =>
            AuthenticationSettings.Create(provider, "client-id", "secret", authority: null, null, null, true));
    }

    [Theory]
    [InlineData(AuthProvider.GenericOidc)]
    [InlineData(AuthProvider.Clerk)]
    public void Create_ForOidcProviders_UsesTheSuppliedAuthority(AuthProvider provider)
    {
        var settings = AuthenticationSettings.Create(provider, "client-id", "secret", "https://issuer.example.com", null, null, true);

        Assert.Equal("https://issuer.example.com", settings.ResolveAuthority());
    }

    [Fact]
    public void Update_WithABlankClientSecret_KeepsTheCurrentlyStoredOne_RatherThanClearingIt()
    {
        var settings = AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "original-secret", null, null, null, true);

        settings.Update(AuthProvider.GoogleWorkspace, "client-id", clientSecret: "", null, null, "example.com", true);

        Assert.Equal("original-secret", settings.ClientSecret);
        Assert.Equal("example.com", settings.HostedDomain);
    }

    [Fact]
    public void Update_WithANewNonBlankClientSecret_ReplacesTheStoredOne()
    {
        var settings = AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "original-secret", null, null, null, true);

        settings.Update(AuthProvider.GoogleWorkspace, "client-id", "new-secret", null, null, null, true);

        Assert.Equal("new-secret", settings.ClientSecret);
    }

    [Fact]
    public void Update_SwitchingFromMicrosoftEntraToGoogleWorkspace_ClearsTheStoredTenantId()
    {
        var settings = AuthenticationSettings.Create(AuthProvider.MicrosoftEntra, "client-id", "secret", null, "contoso-tenant", null, true);

        settings.Update(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true);

        Assert.Null(settings.TenantId);
    }

    [Fact]
    public void Update_SwitchingFromGoogleWorkspaceToGenericOidc_ClearsTheStoredHostedDomain()
    {
        var settings = AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, "example.com", true);

        settings.Update(AuthProvider.GenericOidc, "client-id", "secret", "https://issuer.example.com", null, null, true);

        Assert.Null(settings.HostedDomain);
        Assert.Equal("https://issuer.example.com", settings.Authority);
    }
}
