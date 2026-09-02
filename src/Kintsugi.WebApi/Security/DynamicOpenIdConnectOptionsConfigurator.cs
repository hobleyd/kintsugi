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
/// following a settings save (see <c>AdminSettingsController.UpdateAuthentication</c>) — not on every
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
        // — see AuthenticationMiddleware. That initialization runs OpenIdConnectOptions.Validate,
        // which throws if Authority/MetadataAddress/Configuration/ConfigurationManager are all
        // unset *and* separately if ClientId is unset — which is what they are before anything has
        // ever been saved on the Authentication settings page. Both need a placeholder up front,
        // or the app 500s on every request pre-setup, including /health and the redirect to
        // /settings/authentication that is the only way to configure it: a fresh deploy that
        // cannot be set up at all.
        //
        // Harmless because neither value can reach a provider — but what guarantees that has
        // moved, and the reasoning is easy to get wrong. Only a challenge would send them, and it
        // used to be Program.cs's fresh-deploy redirect that stopped one: with no settings saved it
        // answered every non-/api route, the old Razor Account/Login challenge handler included,
        // with /settings/authentication before this handler ever ran. That redirect is gone (the
        // admin UI is a Flutter client served by nginx, so there is no page request here to
        // redirect), and the challenge route is now GET /api/auth/challenge — which is under /api
        // and was never covered by it anyway. SessionController.Challenge therefore refuses
        // explicitly when nothing has been saved. Keep that check: it is the only thing standing
        // between an unconfigured server and a redirect to Google bearing "kintsugi-unconfigured".
        options.Authority = "https://accounts.google.com";
        options.ClientId = "kintsugi-unconfigured";
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

        // ResolveAuthority returns null for a row whose provider needs a TenantId or Authority and
        // has neither — which the validator and AuthenticationSettings.Apply both refuse to save,
        // but a row written before those rules, or edited in the database, can still look like.
        // Assigning that null would clear the placeholder above and crash every request in the
        // same way, so keep the placeholder instead: sign-in is broken either way, but the
        // settings page stays reachable to fix it.
        var authority = settings.ResolveAuthority();
        if (!string.IsNullOrWhiteSpace(authority))
        {
            options.Authority = authority;
        }

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
