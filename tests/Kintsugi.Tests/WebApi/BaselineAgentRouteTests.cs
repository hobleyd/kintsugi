using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Kintsugi.Application.Baselines.Commands.ReportBaselineResults;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// Both halves of the agent-route checklist for <c>GET /api/baseline</c> and <c>POST /api/baseline-results</c>:
/// nginx demands a fleet certificate on the path, and <c>[RequireAgentIdentity]</c> refuses a certificate
/// whose CN is not the host the request claims to be. Nothing else checks either.
/// </summary>
public class BaselineAgentRouteTests : IClassFixture<ControllerRouteGateTests.RouteTableFactory>
{
    private readonly ControllerRouteGateTests.RouteTableFactory _factory;

    public BaselineAgentRouteTests(ControllerRouteGateTests.RouteTableFactory factory) => _factory = factory;

    private static int? Run(string certificateCn, string argumentName, object argument)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers[RequireAgentIdentityAttribute.CertificateCnHeader] = certificateCn;
        http.Request.Headers[RequireAgentIdentityAttribute.CertificateVerifiedHeader] = "SUCCESS";
        var context = new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?> { [argumentName] = argument },
            controller: new object());

        new RequireAgentIdentityAttribute().OnActionExecuting(context);
        return context.Result is null ? null : (context.Result as ObjectResult)?.StatusCode ?? -1;
    }

    [Fact]
    public void Report_WhoseBodySerialIsNotTheCertificatesCn_Is403()
    {
        var report = new ReportBaselineResultsCommand("SERIAL-1", Array.Empty<BaselineResultInput>());

        Assert.Equal(StatusCodes.Status403Forbidden, Run("SOMEONE-ELSE", "command", report));
        Assert.Null(Run("SERIAL-1", "command", report));
    }

    [Fact]
    public void Fetch_WhoseQuerySerialIsNotTheCertificatesCn_Is403()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, Run("SOMEONE-ELSE", "serialNumber", "SERIAL-1"));
        Assert.Null(Run("SERIAL-1", "serialNumber", "SERIAL-1"));
    }

    /// <summary>The filter above only protects a route that carries it: read the route table the app serves.</summary>
    [Theory]
    [InlineData("api/baseline", "GET")]
    [InlineData("api/baseline-results", "POST")]
    public void TheRouteCarriesRequireAgentIdentity(string pattern, string method)
    {
        var endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText?.TrimStart('/') == pattern
                && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

        Assert.NotNull(endpoint.Metadata.GetMetadata<RequireAgentIdentityAttribute>());
    }

    [Theory]
    [InlineData("/api/baseline")]
    [InlineData("/api/baseline-results")]
    [InlineData("/api/Baseline-Results/")]
    public void NginxRequiresAFleetCertificateOnTheRoute(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kintsugi.sln")))
        {
            directory = directory.Parent;
        }

        var config = File.ReadAllText(Path.Combine(directory!.FullName, "nginx", "default.conf"));
        var agentLocation = Regex.Match(config, @"location ~\* (\^/api/\(host\|[^ ]+) \{");
        Assert.True(agentLocation.Success, "the agent-certificate location was not found in nginx/default.conf");

        Assert.Matches(new Regex(agentLocation.Groups[1].Value, RegexOptions.IgnoreCase), path);
    }
}
