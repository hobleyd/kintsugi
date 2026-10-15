using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.GitHub;
using Kintsugi.Application.GitHub.Commands.CompleteGitHubAppManifest;
using Kintsugi.Application.GitHub.Commands.DisconnectGitHubApp;
using Kintsugi.Application.GitHub.Commands.RecordGitHubAppInstallation;
using Kintsugi.Domain.Exceptions;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// Creating, installing and disconnecting the GitHub App that replaces the two stored GitHub tokens —
/// GitHub's manifest flow, driven from Settings &gt; GitHub.
/// </summary>
/// <remarks>
/// <para>
/// Three legs, and the browser leaves this server between each: <c>manifest</c> hands the page a
/// form to post to GitHub; GitHub sends the browser back to <c>callback</c> with a one-time code,
/// which is converted into the App's credentials and stored, and the browser is sent on to GitHub's
/// installation page; GitHub then sends it back to <c>installed</c> with the installation id.
/// </para>
/// <para>
/// All of it carries <see cref="RequireAdminSessionAttribute"/> on the class, including the two
/// redirect targets — they write a credential into the database, so they must be as gated as the
/// settings save. That works because they are top-level GET navigations, on which the browser sends
/// the session cookie (it is <c>SameSite=Lax</c>); and they sit under the <c>/api</c> nginx location
/// already, so no <c>default.conf</c> change was needed.
/// </para>
/// <para>
/// <c>state</c> on the first redirect is a Data Protection payload with a thirty-minute life: only
/// this server can mint one, so a <c>callback</c> carrying a code from someone else's manifest flow
/// is refused rather than storing an App an attacker created. The installation leg needs no such
/// check — <see cref="RecordGitHubAppInstallationCommand"/> asks GitHub, as the App, whether the
/// installation is the App's own, which is stronger than any state this server could carry, and
/// lets an installation started from GitHub's own App page (which carries no state) land too.
/// </para>
/// <para>
/// Failures on the two redirect legs come back as a redirect to the Settings page with the message
/// in <c>githubAppError</c>, not as problem JSON — the browser is mid-navigation, and a JSON body
/// would be the page the administrator is left looking at.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/settings/github/app")]
[RequireAdminSession]
public class GitHubAppController : ControllerBase
{
    /// <summary>The Settings page both redirect legs return to — <c>AppRouter.settingsGitHub</c> in
    /// <c>web/lib/core/router/app_router.dart</c>.</summary>
    public const string SettingsPagePath = "/settings/github";

    private const string StatePurpose = "Kintsugi.GitHubApp.ManifestState";
    private const string StatePayload = "github-app-manifest";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(30);

    /// <summary>A GitHub organisation login: 1–39 alphanumerics and single hyphens. Checked
    /// because it is interpolated into the create URL's path.</summary>
    private static readonly Regex OrganizationPattern = new("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$");

    private readonly ISender _sender;
    private readonly ITimeLimitedDataProtector _stateProtector;
    private readonly ILogger<GitHubAppController> _logger;

    public GitHubAppController(
        ISender sender, IDataProtectionProvider dataProtectionProvider, ILogger<GitHubAppController> logger)
    {
        _sender = sender;
        _stateProtector = dataProtectionProvider.CreateProtector(StatePurpose).ToTimeLimitedDataProtector();
        _logger = logger;
    }

    /// <summary>The form the Settings page posts to GitHub to create the App.</summary>
    /// <param name="organization">The organisation to create the App under. Blank creates it under
    /// the administrator's personal account, which works but ties the App to that person.</param>
    /// <param name="name">The App's name; blank proposes one from this server's host.</param>
    [HttpGet("manifest")]
    [ProducesResponseType(typeof(GitHubAppManifestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<GitHubAppManifestDto> GetManifest([FromQuery] string? organization, [FromQuery] string? name)
    {
        if (!string.IsNullOrWhiteSpace(organization) && !OrganizationPattern.IsMatch(organization.Trim()))
        {
            return Problem(
                title: "That is not a GitHub organisation name.",
                detail: "Enter the organisation's login as it appears in its URL — github.com/<organisation>.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var appName = string.IsNullOrWhiteSpace(name) ? GitHubAppManifest.DefaultName(Request.Host.Host) : name.Trim();
        if (appName.Length > GitHubAppManifest.MaxNameLength)
        {
            return Problem(
                title: "That App name is too long.",
                detail: $"GitHub allows at most {GitHubAppManifest.MaxNameLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The address the browser reached this server on — the same derivation SessionController's
        // OIDC callback uses, and for the same reason: GitHub sends the browser back to it.
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var manifest = GitHubAppManifest.Build(
            appName, baseUrl, $"{baseUrl}/api/admin/settings/github/app/callback", $"{baseUrl}/api/admin/settings/github/app/installed");
        var state = _stateProtector.Protect(StatePayload, StateLifetime);

        return Ok(new GitHubAppManifestDto(GitHubAppManifest.CreateUrl(organization, state), manifest));
    }

    /// <summary>GitHub's redirect after the App is created: stores its credentials and sends the
    /// browser on to install it.</summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, CancellationToken cancellationToken)
    {
        if (!IsOurState(state))
        {
            return BackToSettings(
                "This GitHub App setup did not start here, or took longer than 30 minutes. Start it again from this page.");
        }

        try
        {
            var slug = await _sender.Send(new CompleteGitHubAppManifestCommand(code ?? string.Empty), cancellationToken);
            return Redirect(GitHubAppManifest.InstallUrl(slug));
        }
        catch (Exception ex) when (ex is ExternalServiceException or DomainException or ValidationException)
        {
            _logger.LogWarning("GitHub App manifest conversion failed: {Reason}", ex.Message);
            return BackToSettings(ex.Message);
        }
    }

    /// <summary>GitHub's redirect after the App is installed (or its installation changed).</summary>
    /// <param name="installationId">GitHub's <c>installation_id</c>.</param>
    /// <param name="setupAction"><c>install</c>, <c>update</c>, or <c>request</c> when a member
    /// without rights asked an owner to install it.</param>
    [HttpGet("installed")]
    public async Task<IActionResult> Installed(
        [FromQuery(Name = "installation_id")] long? installationId,
        [FromQuery(Name = "setup_action")] string? setupAction,
        CancellationToken cancellationToken)
    {
        if (string.Equals(setupAction, "request", StringComparison.OrdinalIgnoreCase))
        {
            return BackToSettings(
                "GitHub has asked an organisation owner to approve the installation. It will take over from the stored tokens once installed.");
        }

        if (installationId is null or <= 0)
        {
            return BackToSettings("GitHub returned without an installation id. Install the App again from this page.");
        }

        try
        {
            await _sender.Send(new RecordGitHubAppInstallationCommand(installationId.Value), cancellationToken);
            return Redirect($"{SettingsPagePath}?githubApp=installed");
        }
        catch (Exception ex) when (ex is ExternalServiceException or DomainException or ValidationException)
        {
            _logger.LogWarning("Recording GitHub App installation {InstallationId} failed: {Reason}", installationId, ex.Message);
            return BackToSettings(ex.Message);
        }
    }

    /// <summary>Forgets the App; the stored tokens apply again.</summary>
    [HttpDelete]
    [ProducesResponseType(typeof(GitHubSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<GitHubSettingsDto>> Disconnect(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new DisconnectGitHubAppCommand(), cancellationToken));

    private bool IsOurState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        try
        {
            return _stateProtector.Unprotect(state) == StatePayload;
        }
        catch (CryptographicException)
        {
            // Tampered, minted by another server, or expired — all the same answer.
            return false;
        }
    }

    private RedirectResult BackToSettings(string error) =>
        Redirect($"{SettingsPagePath}?githubAppError={Uri.EscapeDataString(error)}");
}
