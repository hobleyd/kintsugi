using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Authentication;
using Kintsugi.Application.Authentication.Queries.GetAuthenticationSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// What the browser client needs before it can render anything, and the two ends of the sign-in
/// round trip it cannot perform itself.
/// </summary>
/// <remarks>
/// <para>
/// The admin UI is a Flutter web application served as static files by nginx, so the three things
/// <c>Program.cs</c>'s middleware used to do by <em>redirecting</em> a page request — send a fresh
/// deploy to the authentication settings page, send an unauthenticated visitor to a sign-in page,
/// and let a signed-in one through — cannot work that way any more: the page load never reaches
/// this application at all. <see cref="Get"/> reports that same state as data instead, and the
/// client routes on it.
/// </para>
/// <para>
/// The token exchange stays here rather than moving into Dart. A browser-side authorization-code
/// flow would make this a public client, and <see cref="Kintsugi.Domain.Entities.AuthenticationSettings"/>
/// requires a client secret precisely because it is a confidential one — the secret must not reach
/// the browser, and Google's web-application clients require it at the token endpoint regardless.
/// So <see cref="Challenge"/> is a full-page navigation target that hands off to the provider, the
/// provider comes back to <c>/signin-oidc</c> (still handled by the OpenIdConnect handler), the
/// cookie <see cref="Filters.RequireAdminSessionAttribute"/> reads is set there, and the browser
/// lands back on a client route.
/// </para>
/// </remarks>
[ApiController]
[Produces("application/json")]
public class SessionController : ControllerBase
{
    private readonly ISender _sender;
    private readonly IAuthenticationSettingsRepository _authenticationSettings;

    public SessionController(ISender sender, IAuthenticationSettingsRepository authenticationSettings)
    {
        _sender = sender;
        _authenticationSettings = authenticationSettings;
    }

    /// <summary>Reports whether sign-in is configured, whether it is required, and whether this
    /// caller has done it.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately anonymous, and the only new route that is.</strong> Every other
    /// browser-driven route carries <see cref="Filters.RequireAdminSessionAttribute"/>, because
    /// nginx's client-certificate regex is an exact match that never covers them and
    /// <c>Program.cs</c> exempts all of <c>/api</c> from the sign-in gate. This one cannot: it is
    /// the route that tells a caller whether to sign in, so gating it behind being signed in would
    /// leave a fresh deploy — which has no identity provider configured yet — with no way to reach
    /// the page that configures one.
    /// </para>
    /// <para>
    /// It discloses only whether authentication is configured and enabled, the provider's display
    /// name, and this caller's own identity. No client ID, no secret, nothing about any host.
    /// Keep it that way: anything added here is readable by anyone who can reach the server.
    /// </para>
    /// </remarks>
    [HttpGet("api/session")]
    [ProducesResponseType(typeof(SessionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionDto>> Get(CancellationToken cancellationToken)
    {
        // Read through the repository rather than GetAuthenticationSettingsQuery, which flattens
        // "no row saved" into a NotConfigured DTO indistinguishable from a saved-but-disabled row.
        // The client needs exactly that difference: no row means lock everything to the
        // Authentication settings screen, whereas a disabled row means an administrator has
        // deliberately chosen to run this site open. This is the same test — and the same
        // repository — Program.cs's gate applied before it stopped being able to reach a page load,
        // and RequireAdminSessionAttribute still applies.
        var stored = await _authenticationSettings.GetAsync(cancellationToken);
        var settings = stored is null
            ? AuthenticationSettingsDto.NotConfigured()
            : AuthenticationSettingsDto.FromEntity(stored);

        return Ok(new SessionDto(
            stored is not null,
            settings.IsEnabled,
            User.Identity?.IsAuthenticated == true,
            User.Identity?.Name ?? User.FindFirst("email")?.Value,
            DescribeProvider(settings.Provider),
            settings.IsEnabled && settings.HasClientSecret,
            CallbackUrl,
            SignOutCallbackUrl));
    }

    /// <summary>
    /// Hands off to the identity provider. A full-page navigation target, not an XHR — the
    /// response is a redirect to another origin, which a fetch cannot usefully follow.
    /// </summary>
    /// <remarks>
    /// The guard below used to be somewhere else. <see cref="Security.DynamicOpenIdConnectOptionsConfigurator"/>
    /// seeds a placeholder authority and client ID so the OpenIdConnect handler can initialize on a
    /// server that has never been configured (without them it throws on every request, including
    /// <c>/health</c>), and it argued those could never reach a provider because
    /// <c>Program.cs</c>'s fresh-deploy redirect answered every non-<c>/api</c> route — the old
    /// Razor challenge handler included — before the handler ran. This route is under <c>/api</c>
    /// and that redirect is gone, so the argument no longer holds and the check has to be made
    /// here explicitly.
    /// </remarks>
    [HttpGet("api/auth/challenge")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Challenge([FromQuery] string? returnUrl, CancellationToken cancellationToken)
    {
        var settings = await _sender.Send(new GetAuthenticationSettingsQuery(), cancellationToken);
        if (!settings.HasClientSecret || string.IsNullOrWhiteSpace(settings.ClientId))
        {
            return Problem(
                title: "Sign-in is not configured.",
                detail: "No identity provider has been saved yet. Configure one on the Authentication settings screen.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Url.IsLocalUrl keeps this from being an open redirect. Every client route is an ordinary
        // local path ("/hosts", "/settings/github"), so nothing legitimate is rejected by it.
        var safeReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/hosts";
        return base.Challenge(
            new AuthenticationProperties { RedirectUri = safeReturnUrl },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    /// <summary>Signs out of the cookie and of the provider.</summary>
    /// <remarks>
    /// Signing out of <see cref="OpenIdConnectDefaults.AuthenticationScheme"/> as well as the
    /// cookie is what starts the round trip through the provider's own end-session endpoint, which
    /// is why <see cref="SessionDto.SignOutCallbackUrl"/> has to be registered there — without it
    /// the provider rejects the sign-out. Not gated: refusing to sign out someone who cannot prove
    /// they are signed in protects nothing.
    /// </remarks>
    [HttpPost("api/auth/logout")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public IActionResult Logout() =>
        SignOut(
            new AuthenticationProperties { RedirectUri = "/login" },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme);

    /// <summary>Where the provider sends the browser back to after a successful sign-in. Fixed in
    /// <see cref="Security.DynamicOpenIdConnectOptionsConfigurator"/>; surfaced so the settings
    /// screen can show it for pasting into a provider's app registration.</summary>
    private string CallbackUrl => $"{Request.Scheme}://{Request.Host}/signin-oidc";

    private string SignOutCallbackUrl => $"{Request.Scheme}://{Request.Host}/signout-callback-oidc";

    private static string DescribeProvider(AuthProvider provider) => provider switch
    {
        AuthProvider.GoogleWorkspace => "Google Workspace",
        AuthProvider.MicrosoftEntra => "Microsoft Entra",
        AuthProvider.Clerk => "Clerk",
        _ => "single sign-on"
    };
}

/// <param name="AuthenticationSettingsSaved">Whether an identity provider has ever been saved.
/// False on a fresh deploy, where the client locks itself to the Authentication settings screen —
/// there is no way to sign in and no administrator has decided whether sign-in is required, so
/// leaving the rest of the UI reachable would be the wrong default in the other direction.</param>
/// <param name="AuthenticationEnabled">Whether sign-in is required to use the site.</param>
/// <param name="SignedIn">Whether this caller holds a valid session cookie.</param>
/// <param name="UserName">This caller's display name, or null when not signed in.</param>
/// <param name="ProviderDisplayName">What to call the provider on the sign-in screen.</param>
/// <param name="CanSignIn">Whether the sign-in button should do anything: sign-in is both enabled
/// and completely enough configured to challenge. Mirrors what the old Razor login page called
/// <c>IsConfigured</c>.</param>
/// <param name="CallbackUrl">The redirect URI to register with the provider.</param>
/// <param name="SignOutCallbackUrl">The post-sign-out redirect URI to register with the provider.
/// Not optional — see <see cref="SessionController.Logout"/>.</param>
public record SessionDto(
    bool AuthenticationSettingsSaved,
    bool AuthenticationEnabled,
    bool SignedIn,
    string? UserName,
    string ProviderDisplayName,
    bool CanSignIn,
    string CallbackUrl,
    string SignOutCallbackUrl);
