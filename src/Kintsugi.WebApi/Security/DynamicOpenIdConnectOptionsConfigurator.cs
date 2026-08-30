using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.WebApi.Security;

/// <summary>
/// The OIDC handler's options are normally fixed at startup, but the identity provider here is
/// configured at runtime through the Authentication settings page and stored in the database.
/// This loads that configuration into <see cref="OpenIdConnectOptions"/> the first time the
/// "OpenIdConnect" scheme is used after startup, or after the options cache is invalidated
/// following a settings save (see <c>AuthenticationModel</c> in Pages/Settings) — not on every
/// request, since <see cref="IOptionsMonitorCache{TOptions}"/> caches the constructed options
/// until then.
/// </summary>
public class DynamicOpenIdConnectOptionsConfigurator : IConfigureNamedOptions<OpenIdConnectOptions>
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DynamicOpenIdConnectOptionsConfigurator(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != OpenIdConnectDefaults.AuthenticationScheme)
        {
            return;
        }

        // OpenIdConnectHandler is a remote *request handler* scheme, so ASP.NET Core initializes
        // it on every request (not just /signin-oidc) to ask whether this request is its callback
        // — see AuthenticationMiddleware. That initialization throws immediately if Authority,
        // MetadataAddress, Configuration, and ConfigurationManager are all unset, which they are
        // before anything has ever been saved on the Authentication settings page. Set a
        // resolvable placeholder up front so the app doesn't crash on every request pre-setup;
        // it's harmless since nothing can trigger an actual challenge until settings exist, and is
        // overwritten below once they do.
        options.Authority = "https://accounts.google.com";
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthenticationSettingsRepository>();

        // IConfigureOptions.Configure is a synchronous callback with no async equivalent in this
        // pipeline, and this only runs once per settings change (see class remarks above) rather
        // than per-request, so the blocking call here is acceptable.
        var settings = repository.GetAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (settings is null)
        {
            return;
        }

        options.ClientId = settings.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.Authority = settings.ResolveAuthority();

        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");

        if (settings.Provider == AuthProvider.GoogleWorkspace && !string.IsNullOrWhiteSpace(settings.HostedDomain))
        {
            // Restricts Google's account chooser/consent screen to the configured Workspace
            // domain; still validated again below since the "hd" hint alone isn't enforcement.
            var hostedDomain = settings.HostedDomain;
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.SetParameter("hd", hostedDomain);
                return Task.CompletedTask;
            };

            options.Events.OnTokenValidated = context =>
            {
                var actualDomain = context.Principal?.FindFirst("hd")?.Value;
                if (!string.Equals(actualDomain, hostedDomain, StringComparison.OrdinalIgnoreCase))
                {
                    context.Fail($"Account does not belong to the {hostedDomain} Google Workspace domain.");
                }

                return Task.CompletedTask;
            };
        }
    }

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);
}
