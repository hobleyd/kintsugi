using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.WebApi.Filters;

/// <summary>
/// Requires the caller to be a signed-in administrator, for a browser-driven <c>/api</c> route that
/// changes what agents will execute.
/// </summary>
/// <remarks>
/// <para>
/// This exists because such a route is otherwise reachable by anyone who can reach the server, and
/// neither of the two mechanisms that look like they would stop it actually does. nginx requires an
/// agent client certificate on an <em>exact-match</em> regex
/// (<c>^/api/(host|applications|patching-policy|upgrade-paths|...)$</c>), so
/// <c>/api/upgrade-paths/sign-script</c> never matches it — deliberately, since these routes are
/// driven by the admin UI's JavaScript and a browser has no agent certificate. And
/// <c>Program.cs</c>'s sign-in gate exempts the whole of <c>/api</c>, on the reasoning that agents
/// authenticate with mutual TLS rather than cookies. Each decision is individually correct; together
/// they leave a browser-driven mutation route with no authentication of any kind.
/// </para>
/// <para>
/// That mattered most for the pair <c>save</c> and <c>sign-script</c>: the first accepts an arbitrary
/// script, the second has the server sign it with the artifact-signing key, and every agent in the
/// fleet then runs signed content as root. The human-review property the signing model rests on was
/// enforced only by the admin UI being the sole thing that happened to call these routes.
/// </para>
/// <para>
/// Semantics mirror <c>Program.cs</c>'s own gate exactly, rather than inventing a second shape that
/// could drift from it: authentication is required precisely when an administrator has saved
/// <c>AuthenticationSettings</c> and enabled it. When it is disabled, the admin has deliberately
/// chosen to run the site open and this route is no more exposed than the pages that do the same
/// thing. When nothing has been saved at all — a fresh deploy, which redirects the whole browser UI
/// to <c>/settings/authentication</c> — this allows the call, matching that same posture; a server in
/// that state has no enrolled agents to attack yet, and failing closed would leave no way to reach a
/// first-run state that needs no protecting.
/// </para>
/// <para>
/// Resolves its repository from <c>HttpContext.RequestServices</c> rather than by constructor
/// injection, because an attribute is instantiated once per action and cannot hold a scoped
/// dependency — the same reason <c>Program.cs</c>'s middleware resolves it per request.
/// </para>
/// </remarks>
public class RequireAdminSessionAttribute : ActionFilterAttribute
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var settingsRepository = context.HttpContext.RequestServices.GetRequiredService<IAuthenticationSettingsRepository>();
        var settings = await settingsRepository.GetAsync(context.HttpContext.RequestAborted);

        if (settings?.IsEnabled == true && context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Not signed in.",
                Detail = "This action changes what agents will execute and requires a signed-in administrator."
            })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return;
        }

        await next();
    }
}
