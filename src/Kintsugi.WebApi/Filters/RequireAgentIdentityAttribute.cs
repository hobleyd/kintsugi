using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.WebApi.Filters;

/// <summary>
/// The second half of proving "this request is really from the host it claims to be": nginx
/// already required a client certificate signed by the agent fleet CA to reach this route at all
/// (see nginx/default.conf) and forwards its verified Subject CN — always a serial number, set by
/// <c>CaService</c> at enrollment — as <c>X-Agent-Cert-Cn</c>. This attribute checks that header
/// against the serial number the request body/query actually claims, so a compromised agent can't
/// present its own valid certificate while reporting data for a *different* host.
///
/// Fails closed: a missing or empty header is treated the same as a mismatch. In the deployed
/// topology the api service is unreachable except through nginx (see docker-compose.yml), so the
/// header is always either present-and-verified or the request couldn't have arrived at all — a
/// missing header here means something is misconfigured, not that this is a legitimately
/// unauthenticated caller.
/// </summary>
public class RequireAgentIdentityAttribute : ActionFilterAttribute
{
    public const string CertificateCnHeader = "X-Agent-Cert-Cn";

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var certificateCn = context.HttpContext.Request.Headers[CertificateCnHeader].ToString();

        var claimedSerialNumber = context.ActionArguments.Values.OfType<IAgentScopedRequest>().FirstOrDefault()?.SerialNumber
            ?? context.ActionArguments
                .FirstOrDefault(a => string.Equals(a.Key, "serialNumber", StringComparison.OrdinalIgnoreCase)).Value as string;

        if (string.IsNullOrEmpty(certificateCn) ||
            string.IsNullOrEmpty(claimedSerialNumber) ||
            !string.Equals(certificateCn, claimedSerialNumber, StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Request not authorized.",
                Detail = "The agent certificate's identity does not match the host this request claims to be."
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        base.OnActionExecuting(context);
    }
}
