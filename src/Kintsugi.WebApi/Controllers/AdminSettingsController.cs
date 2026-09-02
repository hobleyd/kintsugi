using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Kintsugi.Application.Authentication;
using Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;
using Kintsugi.Application.Authentication.Queries.GetAuthenticationSettings;
using Kintsugi.Application.GitHub;
using Kintsugi.Application.GitHub.Commands.UpdateGitHubSettings;
using Kintsugi.Application.GitHub.Queries.GetGitHubSettings;
using Kintsugi.Application.PatchingPolicy;
using Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;
using Kintsugi.Application.PatchingPolicy.Queries.GetPatchingPolicySettings;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The three settings screens that had no REST surface of their own — authentication, GitHub and
/// patching policy. (The AI agent's already has one: see <see cref="AiSettingsController"/>.)
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

    public AdminSettingsController(
        ISender sender,
        IOptionsMonitorCache<OpenIdConnectOptions> oidcOptionsCache)
    {
        _sender = sender;
        _oidcOptionsCache = oidcOptionsCache;
    }

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
        var result = await _sender.Send(command, cancellationToken);
        _oidcOptionsCache.TryRemove(OpenIdConnectDefaults.AuthenticationScheme);
        return Ok(result);
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
}
