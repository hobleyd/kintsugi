using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Kintsugi.WebApi.Filters;

/// <summary>
/// For an agent route that has no host to bind the caller to: requires only that nginx verified a
/// fleet certificate on this request (<see cref="RequireAgentIdentityAttribute.NginxVerifiedACertificate"/>),
/// without comparing its CN with a claimed serial number.
/// </summary>
/// <remarks>
/// <para>
/// <c>GET /api/patching-policy</c> is the case. The policy is fleet-wide — every host gets the same
/// answer — and all three agents poll it with no <c>?serialNumber=</c> (see each agent's
/// <c>policy.rs</c>), so <see cref="RequireAgentIdentityAttribute"/> there would find no claimed serial
/// and refuse every agent in the fleet. Binding to a host would add nothing anyway; what matters is
/// that the caller is an enrolled agent rather than anyone who can reach the server.
/// </para>
/// <para>
/// Like that attribute, this is the application's half of a check nginx makes first: the route is
/// in nginx's agent regex, which refuses a request with no verified certificate before it gets here.
/// It exists so the route does not rely on nginx alone — while that regex was case-sensitive,
/// <c>/api/Patching-Policy</c> reached this action with no certificate at all.
/// </para>
/// </remarks>
public class RequireAgentCertificateAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!RequireAgentIdentityAttribute.NginxVerifiedACertificate(context.HttpContext.Request))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Request not authorized.",
                Detail = "A verified agent client certificate is required."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        base.OnActionExecuting(context);
    }
}
