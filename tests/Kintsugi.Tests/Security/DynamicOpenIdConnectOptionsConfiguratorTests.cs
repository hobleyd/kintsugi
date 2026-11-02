using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Kintsugi.Application.Authentication;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.Security;

namespace Kintsugi.Tests.Security;

/// <summary>
/// What the OIDC options do at sign-in that <see cref="AdminIdentity.VerifiedEmailOf"/> depends on:
/// the provider is recorded on the identity (and cannot be supplied by the provider itself), and
/// <c>email_verified</c> is mapped from the userinfo endpoint.
/// </summary>
public class DynamicOpenIdConnectOptionsConfiguratorTests
{
    private static OpenIdConnectOptions Configure(AuthenticationSettings settings)
    {
        var repository = new Mock<IAuthenticationSettingsRepository>();
        repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        var services = new ServiceCollection().AddScoped(_ => repository.Object).BuildServiceProvider();

        var options = new OpenIdConnectOptions();
        new DynamicOpenIdConnectOptionsConfigurator(services.GetRequiredService<IServiceScopeFactory>())
            .Configure(OpenIdConnectDefaults.AuthenticationScheme, options);
        return options;
    }

    private static async Task<TokenValidatedContext> SignIn(OpenIdConnectOptions options, params Claim[] claims)
    {
        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            options,
            new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
            new AuthenticationProperties());
        await options.Events.OnTokenValidated(context);
        return context;
    }

    private static AuthenticationSettings Settings(AuthProvider provider, string? hostedDomain = null) => provider switch
    {
        AuthProvider.MicrosoftEntra => AuthenticationSettings.Create(provider, "client-id", "secret", null, "tenant-id", null, true),
        AuthProvider.GenericOidc or AuthProvider.Clerk => AuthenticationSettings.Create(provider, "client-id", "secret", "https://idp.example.invalid", null, null, true),
        _ => AuthenticationSettings.Create(provider, "client-id", "secret", null, null, hostedDomain, true),
    };

    [Theory]
    [InlineData(AuthProvider.GoogleWorkspace)]
    [InlineData(AuthProvider.MicrosoftEntra)]
    [InlineData(AuthProvider.GenericOidc)]
    [InlineData(AuthProvider.Clerk)]
    public async Task SignIn_RecordsTheProvider(AuthProvider provider)
    {
        var context = await SignIn(Configure(Settings(provider)), new Claim("email", "a@example.invalid"));

        Assert.Equal(provider.ToString(), Assert.Single(context.Principal!.FindAll(AdminIdentity.ProviderClaim)).Value);
    }

    // With MapInboundClaims off every id_token claim lands on the identity, so a provider that sent
    // its own kintsugi_idp could otherwise pass for Entra and skip email_verified.
    [Fact]
    public async Task ProviderSuppliedClaim_IsReplaced()
    {
        var context = await SignIn(
            Configure(Settings(AuthProvider.GenericOidc)),
            new Claim("email", "admin@example.invalid"),
            new Claim(AdminIdentity.ProviderClaim, nameof(AuthProvider.MicrosoftEntra)));

        Assert.Equal(nameof(AuthProvider.GenericOidc), Assert.Single(context.Principal!.FindAll(AdminIdentity.ProviderClaim)).Value);
        Assert.Null(AdminIdentity.VerifiedEmailOf(context.Principal));
    }

    [Fact]
    public async Task HostedDomainMismatch_StillFails()
    {
        var context = await SignIn(Configure(Settings(AuthProvider.GoogleWorkspace, "example.invalid")), new Claim("hd", "other.invalid"));

        Assert.NotNull(context.Result?.Failure);
    }

    [Fact]
    public async Task HostedDomainMatch_Succeeds_AndRecordsTheProvider()
    {
        var context = await SignIn(Configure(Settings(AuthProvider.GoogleWorkspace, "example.invalid")), new Claim("hd", "example.invalid"));

        Assert.Null(context.Result?.Failure);
        Assert.Equal(nameof(AuthProvider.GoogleWorkspace), context.Principal!.FindFirst(AdminIdentity.ProviderClaim)?.Value);
    }

    // Some providers send email_verified only from userinfo, as a JSON boolean — which the claim
    // action renders as "True", the spelling AdminIdentity accepts alongside "true".
    [Fact]
    public void EmailVerified_IsMappedFromUserInfo()
    {
        var options = Configure(Settings(AuthProvider.GenericOidc));
        var identity = new ClaimsIdentity();
        using var userInfo = JsonDocument.Parse("""{"email_verified": true}""");

        foreach (var action in options.ClaimActions)
        {
            action.Run(userInfo.RootElement, identity, "issuer");
        }

        var value = identity.FindFirst("email_verified")?.Value;
        Assert.NotNull(value);
        Assert.Equal("true", value, ignoreCase: true);
    }
}
