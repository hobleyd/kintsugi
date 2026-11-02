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
///
/// Both headers are trustworthy only because nginx sets them in its two certificate locations and
/// clears them in every other location that proxies (see nginx/CLAUDE.md). That clearing is the
/// real control: when the agent regex was case-sensitive, <c>/api/Host/</c> fell through to the
/// general <c>/api</c> block carrying a client-chosen CN, and ASP.NET routed it here. Requiring
/// <c>X-Agent-Cert-Verified == SUCCESS</c> as well is defence in depth, not a fix for that — a
/// location that failed to clear one header would fail to clear both. What it does catch is a
/// location that forwards the CN without nginx's own <c>$ssl_client_verify</c> check, or a switch
/// to <c>ssl_verify_client optional_no_ca</c>: either would hand over the CN of a certificate that
/// did not verify, and Verified would then say so.
/// </summary>
public class RequireAgentIdentityAttribute : ActionFilterAttribute
{
    public const string CertificateCnHeader = "X-Agent-Cert-Cn";
    public const string CertificateVerifiedHeader = "X-Agent-Cert-Verified";

    /// <summary>
    /// Whether nginx verified a fleet certificate on this request: <c>$ssl_client_verify</c> was
    /// <c>SUCCESS</c> and the certificate had a CN. Shared with
    /// <see cref="RequireAgentCertificateAttribute"/>, for a route with no serial number to compare,
    /// so the two cannot drift apart on what counts.
    /// </summary>
    public static bool NginxVerifiedACertificate(HttpRequest request) =>
        string.Equals(request.Headers[CertificateVerifiedHeader].ToString(), "SUCCESS", StringComparison.Ordinal)
        && !string.IsNullOrEmpty(request.Headers[CertificateCnHeader].ToString());

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var certificateCn = context.HttpContext.Request.Headers[CertificateCnHeader].ToString();

        var claimedSerialNumber = context.ActionArguments.Values.OfType<IAgentScopedRequest>().FirstOrDefault()?.SerialNumber
            ?? context.ActionArguments
                .FirstOrDefault(a => string.Equals(a.Key, "serialNumber", StringComparison.OrdinalIgnoreCase)).Value as string;

        if (!NginxVerifiedACertificate(context.HttpContext.Request) ||
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
