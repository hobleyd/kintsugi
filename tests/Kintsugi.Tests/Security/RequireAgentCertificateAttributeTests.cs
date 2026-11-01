using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Kintsugi.WebApi.Controllers;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.Tests.Security;

/// <summary>
/// The certificate-only check on <c>GET /api/patching-policy</c>: an enrolled agent gets through
/// with no serial number in the request — which is how all three agents call it — and nobody else
/// does, whatever headers they send.
/// </summary>
public class RequireAgentCertificateAttributeTests
{
    private static ActionExecutingContext CreateContext(string? cn, string? verified)
    {
        var httpContext = new DefaultHttpContext();
        if (cn is not null)
        {
            httpContext.Request.Headers[RequireAgentIdentityAttribute.CertificateCnHeader] = cn;
        }

        if (verified is not null)
        {
            httpContext.Request.Headers[RequireAgentIdentityAttribute.CertificateVerifiedHeader] = verified;
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
    }

    // No serial number anywhere in the arguments: the agents' policy.rs sends none.
    [Fact]
    public void VerifiedCertificate_WithNoSerialNumber_IsAllowedThrough()
    {
        var context = CreateContext("SERIAL-123", "SUCCESS");

        new RequireAgentCertificateAttribute().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("SERIAL-123", null)]
    [InlineData("SERIAL-123", "NONE")]
    [InlineData("SERIAL-123", "FAILED:certificate has expired")]
    [InlineData(null, "SUCCESS")]
    [InlineData("", "SUCCESS")]
    public void WithoutAVerifiedCertificate_IsRejectedWith403(string? cn, string? verified)
    {
        var context = CreateContext(cn, verified);

        new RequireAgentCertificateAttribute().OnActionExecuting(context);

        Assert.Equal(StatusCodes.Status403Forbidden, (context.Result as ObjectResult)?.StatusCode);
    }

    // The route itself relied on nginx alone; while nginx's agent regex was case-sensitive,
    // /api/Patching-Policy reached this action with no certificate at all.
    [Fact]
    public void PatchingPolicyGet_RequiresAnAgentCertificate()
    {
        var get = typeof(PatchingPolicyController).GetMethod(nameof(PatchingPolicyController.Get))!;

        Assert.NotNull(get.GetCustomAttribute<RequireAgentCertificateAttribute>());
    }
}
