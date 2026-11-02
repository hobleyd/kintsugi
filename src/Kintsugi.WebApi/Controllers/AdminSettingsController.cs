using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Kintsugi.Application.Auditing;
using Kintsugi.Application.Auditing.Commands.UpdateAuditSettings;
using Kintsugi.Application.Auditing.Queries.GetAuditSettings;
using Kintsugi.Application.Authentication;
using Kintsugi.Domain.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;
using Kintsugi.Application.Authentication.Queries.GetAuthenticationSettings;
using Kintsugi.Application.GitHub;
using Kintsugi.Application.GitHub.Commands.UpdateGitHubSettings;
using Kintsugi.Application.GitHub.Queries.GetGitHubSettings;
using Kintsugi.Application.PatchingPolicy;
using Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;
using Kintsugi.Application.PatchingPolicy.Queries.GetPatchingPolicySettings;
using Kintsugi.Application.Vanta;
using Kintsugi.Application.Vanta.Commands.UpdateVantaSettings;
using Kintsugi.Application.Vanta.Queries.GetVantaSettings;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Application.Vulnerabilities.Commands.UpdateVulnerabilitySettings;
using Kintsugi.Application.Vulnerabilities.Queries.GetVulnerabilitySettings;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The settings screens that had no REST surface of their own — auditing, authentication, GitHub,
/// patching policy and Vanta. (The AI agent's already has one: see <see cref="AiSettingsController"/>.)
/// </summary>
/// <remarks>
/// <para>
/// Validation is deliberately not caught here. <c>ValidationBehaviour</c> raises
/// <see cref="FluentValidation.ValidationException"/> and the domain raises
/// <see cref="Kintsugi.Domain.Exceptions.DomainException"/>; both are already turned into
/// <c>application/problem+json</c> by <see cref="Middleware.ExceptionHandlingMiddleware"/>. That
/// is what replaces the <c>ModelState</c> error list the Razor forms rendered, and it means a
/// field-level validation failure arrives at the client as a per-property dictionary rather than a
/// flattened string.
/// </para>
/// <para>
/// No token or secret is ever returned by any route here — the DTOs carry only "one is stored"
/// booleans, which is what lets the client honestly offer "leave blank to keep the existing one".
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/settings")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminSettingsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _oidcOptionsCache;
    private readonly IVantaSyncCoordinator _vantaSyncCoordinator;

    public AdminSettingsController(
        ISender sender,
        IOptionsMonitorCache<OpenIdConnectOptions> oidcOptionsCache,
        IVantaSyncCoordinator vantaSyncCoordinator)
    {
        _sender = sender;
        _oidcOptionsCache = oidcOptionsCache;
        _vantaSyncCoordinator = vantaSyncCoordinator;
    }

    [HttpGet("auditing")]
    [ProducesResponseType(typeof(AuditSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuditSettingsDto>> GetAuditing(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetAuditSettingsQuery(), cancellationToken));

    [HttpPut("auditing")]
    [ProducesResponseType(typeof(AuditSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuditSettingsDto>> UpdateAuditing(
        UpdateAuditSettingsCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));

    [HttpGet("authentication")]
    [ProducesResponseType(typeof(AuthenticationSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthenticationSettingsDto>> GetAuthentication(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetAuthenticationSettingsQuery(), cancellationToken));

    /// <remarks>
    /// The cache eviction at the end is not optional. The OpenIdConnect handler's options are built
    /// once and cached until invalidated (see
    /// <see cref="Security.DynamicOpenIdConnectOptionsConfigurator"/>), so without this the next
    /// sign-in attempt would use the provider, client ID and secret that were current when the
    /// scheme was first exercised — an administrator would save a correct configuration and watch
    /// sign-in keep failing against the old one until the process restarted.
    /// </remarks>
    [HttpPut("authentication")]
    [ProducesResponseType(typeof(AuthenticationSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuthenticationSettingsDto>> UpdateAuthentication(
        UpdateAuthenticationSettingsCommand command, CancellationToken cancellationToken)
    {
        await EnsureCallerKeepsAccess(command, cancellationToken);

        var result = await _sender.Send(command, cancellationToken);
        _oidcOptionsCache.TryRemove(OpenIdConnectDefaults.AuthenticationScheme);
        return Ok(result);
    }

    /// <summary>
    /// Lockout guard: refuses a save after which the caller would not be an administrator, because
    /// the very next request would be a 403 and nobody could repair the list from the UI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checks the list as it will be <em>stored</em>, not as it was sent. A null list means "keep the
    /// stored one", so it is the stored list that has to include the caller; and entries are
    /// normalised by <see cref="AuthenticationSettings.NormaliseAdminEmails"/>, so a null or blank
    /// entry cannot match a caller who has no email. Those were the two ways past the first version
    /// of this check, which only looked at a non-empty list in the request.
    /// </para>
    /// <para>
    /// The same rule as <see cref="RequireAdminSessionAttribute.IsAdministrator"/>: an empty list
    /// admits every signed-in user, otherwise only a listed, verified email. A caller with no
    /// verified email — anyone at all while sign-in is still off — can therefore never be on the
    /// list, so turning sign-in on with an allowlist takes two saves, and the message says so rather
    /// than telling them to add an email they cannot be recognised by yet.
    /// </para>
    /// <para>
    /// The caller's session proves who they are to the <em>current</em> identity source, not to the
    /// one being saved. Switching provider — or the tenant, issuer or hosted domain within one — while
    /// a list is in force would pass the email check above and then lock everyone out once cookies
    /// expire: the new source may not assert <c>email_verified</c>, or may assert a different address.
    /// So that change is refused until the list is cleared, the same way enabling sign-in is.
    /// </para>
    /// </remarks>
    private async Task EnsureCallerKeepsAccess(UpdateAuthenticationSettingsCommand command, CancellationToken cancellationToken)
    {
        if (!command.IsEnabled)
        {
            return;
        }

        var stored = await _sender.Send(new GetAuthenticationSettingsQuery(), cancellationToken);
        IReadOnlyList<string> effective = command.AdminEmails is not null
            ? AuthenticationSettings.NormaliseAdminEmails(command.AdminEmails)
            : stored.AdminEmails;
        if (effective.Count == 0)
        {
            return;
        }

        var callerEmail = AdminIdentity.VerifiedEmailOf(User);
        if (callerEmail is null)
        {
            throw new DomainException(
                "Turning on sign-in with an administrators list takes two saves: first enable sign-in with an empty list, "
                + "sign in, then add the list including your own email.");
        }

        if (!effective.Contains(callerEmail, StringComparer.OrdinalIgnoreCase))
        {
            throw new DomainException("The administrators list must include your own email, or you would lock yourself out.");
        }

        if (stored.IsEnabled && ChangesIdentitySource(stored, command))
        {
            throw new DomainException(
                "Changing the sign-in provider, tenant, issuer or hosted domain while an administrators list is set could lock "
                + "everyone out. Clear the list first, make the change, sign in again, then restore the list.");
        }
    }

    /// <summary>Whether the save points sign-in at a different identity source than the stored one,
    /// comparing only the field each provider actually keeps (see <c>AuthenticationSettings.Update</c>).</summary>
    private static bool ChangesIdentitySource(AuthenticationSettingsDto stored, UpdateAuthenticationSettingsCommand command)
    {
        if (command.Provider != stored.Provider)
        {
            return true;
        }

        static string? Norm(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var (sent, kept) = command.Provider switch
        {
            AuthProvider.GoogleWorkspace => (command.HostedDomain, stored.HostedDomain),
            AuthProvider.MicrosoftEntra => (command.TenantId, stored.TenantId),
            _ => (command.Authority, stored.Authority),
        };
        return !string.Equals(Norm(sent), Norm(kept), StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet("github")]
    [ProducesResponseType(typeof(GitHubSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<GitHubSettingsDto>> GetGitHub(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetGitHubSettingsQuery(), cancellationToken));

    [HttpPut("github")]
    [ProducesResponseType(typeof(GitHubSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GitHubSettingsDto>> UpdateGitHub(
        UpdateGitHubSettingsCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));

    [HttpGet("patching-policy")]
    [ProducesResponseType(typeof(PatchingPolicySettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PatchingPolicySettingsDto>> GetPatchingPolicy(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetPatchingPolicySettingsQuery(), cancellationToken));

    /// <summary>Saves the fleet-wide patching policy.</summary>
    /// <remarks>
    /// <para>
    /// Deliberately <em>not</em> a <c>PUT</c> on <c>/api/patching-policy</c>, where the matching
    /// <c>GET</c> lives. That path sits inside nginx's exact-match agent regex, so a write there
    /// inherits "any valid fleet client certificate is admitted" as its admission rule — which
    /// sounds like protection and is the opposite of it. A <c>PUT</c> there once carried no
    /// <see cref="RequireAgentIdentityAttribute"/> and no admin gate, so any enrolled agent could
    /// rewrite the policy for every other host in the fleet, while a browser could not reach it at
    /// all. It was removed rather than gated; <see cref="PatchingPolicyController"/> keeps the note
    /// explaining why, and says a write route belongs on a path outside that regex carrying
    /// <see cref="RequireAdminSessionAttribute"/>. This is that route.
    /// </para>
    /// <para>
    /// The <c>GET</c> above is a duplicate of the agent-facing one on purpose: the agents' copy is
    /// inside the regex because they need a certificate to reach it, and a browser has none.
    /// </para>
    /// </remarks>
    [HttpPut("patching-policy")]
    [ProducesResponseType(typeof(PatchingPolicySettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PatchingPolicySettingsDto>> UpdatePatchingPolicy(
        UpdatePatchingPolicySettingsCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));

    /// <summary>The vulnerability-assessment configuration. The NVD API key is never returned —
    /// <c>HasNvdApiKey</c> stands in for it, the same way every other secret here is handled.</summary>
    [HttpGet("vulnerabilities")]
    [ProducesResponseType(typeof(VulnerabilitySettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<VulnerabilitySettingsDto>> GetVulnerabilities(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetVulnerabilitySettingsQuery(), cancellationToken));

    [HttpPut("vulnerabilities")]
    [ProducesResponseType(typeof(VulnerabilitySettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VulnerabilitySettingsDto>> UpdateVulnerabilities(
        UpdateVulnerabilitySettingsCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));

    [HttpGet("vanta")]
    [ProducesResponseType(typeof(VantaSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<VantaSettingsDto>> GetVanta(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetVantaSettingsQuery(), cancellationToken));

    [HttpPut("vanta")]
    [ProducesResponseType(typeof(VantaSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VantaSettingsDto>> UpdateVanta(
        UpdateVantaSettingsCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));

    /// <summary>Asks the background service to sync this fleet's patch state to Vanta now.</summary>
    /// <remarks>
    /// Returns immediately with the run's starting status rather than the result: a fleet-wide sync
    /// builds the complete state of the world and uploads it in two requests, which is longer than an
    /// HTTP request should block for. The screen polls <c>vanta/sync-status</c> for the outcome, the
    /// same way the upgrade-path screens poll theirs.
    ///
    /// A <c>409</c> when a run is already in flight, and that is a real constraint rather than
    /// politeness: Vanta issues one access token per application and revokes the previous one each
    /// time a new one is asked for, so two overlapping runs would invalidate each other's
    /// credentials mid-upload.
    /// </remarks>
    [HttpPost("vanta/sync")]
    [ProducesResponseType(typeof(VantaSyncStatusDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<VantaSyncStatusDto> SyncVanta()
    {
        if (!_vantaSyncCoordinator.TryRequestStart())
        {
            return Conflict(new ProblemDetails
            {
                Title = "A Vanta sync is already running.",
                Detail = "Wait for the run in progress to finish before starting another.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        return Accepted(_vantaSyncCoordinator.GetStatus());
    }

    [HttpGet("vanta/sync-status")]
    [ProducesResponseType(typeof(VantaSyncStatusDto), StatusCodes.Status200OK)]
    public ActionResult<VantaSyncStatusDto> GetVantaSyncStatus() => Ok(_vantaSyncCoordinator.GetStatus());
}
