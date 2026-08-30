using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Kintsugi.Application.Hosts.Commands.CreateHost;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.Tests.Security;

/// <summary>
/// Covers the second half of "the two systems are the right ones": nginx already proved a valid
/// agent certificate reached this route at all (see nginx/default.conf); this attribute checks
/// that certificate's identity (forwarded as X-Agent-Cert-Cn) against the identity the request
/// itself claims. <see cref="CreateHostCommand"/> is used as a concrete <c>IAgentScopedRequest</c>
/// here purely because it's a real one already in the codebase, not because these tests are about
/// host creation specifically.
/// </summary>
public class RequireAgentIdentityAttributeTests
{
    private static ActionExecutingContext CreateContext(string? headerValue, IDictionary<string, object?> actionArguments)
    {
        var httpContext = new DefaultHttpContext();
        if (headerValue is not null)
        {
            httpContext.Request.Headers[RequireAgentIdentityAttribute.CertificateCnHeader] = headerValue;
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), actionArguments, controller: new object());
    }

    private static int? ResultStatusCode(ActionExecutingContext context) => (context.Result as ObjectResult)?.StatusCode;

    [Fact]
    public void MatchingCertificateCnAndCommandSerialNumber_IsAllowedThrough()
    {
        var command = new CreateHostCommand("host-1", "SERIAL-123");
        var context = CreateContext("SERIAL-123", new Dictionary<string, object?> { ["command"] = command });

        new RequireAgentIdentityAttribute().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void MismatchedCertificateCn_IsRejectedWith403()
    {
        var command = new CreateHostCommand("host-1", "SERIAL-123");
        var context = CreateContext("SOME-OTHER-SERIAL", new Dictionary<string, object?> { ["command"] = command });

        new RequireAgentIdentityAttribute().OnActionExecuting(context);

        Assert.Equal(StatusCodes.Status403Forbidden, ResultStatusCode(context));
    }

    [Fact]
    public void MissingCertificateHeader_IsRejectedWith403_EvenWithAValidClaimedSerialNumber()
    {
        // Fails closed: in the deployed topology (see docker-compose.yml) the api service is
        // unreachable except through nginx, so an absent header here means something is
        // misconfigured — never a legitimately unauthenticated caller to wave through.
        var command = new CreateHostCommand("host-1", "SERIAL-123");
        var context = CreateContext(headerValue: null, new Dictionary<string, object?> { ["command"] = command });

        new RequireAgentIdentityAttribute().OnActionExecuting(context);

        Assert.Equal(StatusCodes.Status403Forbidden, ResultStatusCode(context));
    }

    [Fact]
    public void MatchingCertificateCnAgainstARawSerialNumberQueryArgument_IsAllowedThrough()
    {
        // Mirrors UpgradePathsController.Get, which binds [FromQuery] string serialNumber
        // directly rather than via an IAgentScopedRequest command object.
        var context = CreateContext("SERIAL-456", new Dictionary<string, object?> { ["serialNumber"] = "SERIAL-456" });

        new RequireAgentIdentityAttribute().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void MismatchedRawSerialNumberQueryArgument_IsRejectedWith403()
    {
        var context = CreateContext("SERIAL-456", new Dictionary<string, object?> { ["serialNumber"] = "SOMEONE-ELSE" });

        new RequireAgentIdentityAttribute().OnActionExecuting(context);

        Assert.Equal(StatusCodes.Status403Forbidden, ResultStatusCode(context));
    }

    [Fact]
    public void NoClaimedIdentityAnywhereInTheArguments_IsRejectedWith403()
    {
        var context = CreateContext("SERIAL-789", new Dictionary<string, object?> { ["somethingElse"] = 42 });

        new RequireAgentIdentityAttribute().OnActionExecuting(context);

        Assert.Equal(StatusCodes.Status403Forbidden, ResultStatusCode(context));
    }
}
