using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Kintsugi.WebApi.Controllers;
using Kintsugi.WebApi.Filters;
using Kintsugi.WebApi.Security;

namespace Kintsugi.Tests.WebApi;

public class AgentPackagesControllerTests
{
    /// <summary>
    /// nginx's <c>/api/agent-packages</c> block checks nothing, so this attribute is the only thing
    /// between an upload and a checksum signed with the key every agent pins. It was absent once.
    /// </summary>
    [Fact]
    public void Publish_RequiresAnAdminSession()
    {
        var publish = typeof(AgentPackagesController).GetMethod(nameof(AgentPackagesController.Publish))!;

        Assert.NotNull(publish.GetCustomAttribute<RequireAdminSessionAttribute>());
        Assert.NotNull(publish.GetCustomAttribute<HttpPostAttribute>());
    }

    /// <summary>The reads stay reachable without a session — an enrolled agent's self-update and
    /// the Clients page both depend on it — so they must not grow the attribute by accident.</summary>
    [Theory]
    [InlineData(nameof(AgentPackagesController.GetAll))]
    [InlineData(nameof(AgentPackagesController.GetLatest))]
    [InlineData(nameof(AgentPackagesController.Download))]
    public void Reads_DoNotRequireAnAdminSession(string action)
    {
        var method = typeof(AgentPackagesController).GetMethod(action)!;

        Assert.Null(method.GetCustomAttribute<RequireAdminSessionAttribute>());
    }

    /// <summary>
    /// A bodiless read inside nginx's certificate regex is gated by the certificate alone, which
    /// proves the caller is <em>an</em> agent and nothing more. The fleet-wide listing that used to
    /// live here was readable by any one host's certificate, so it was removed; this keeps it out.
    /// </summary>
    [Fact]
    public void ApplicationsController_HasNoGetRoutes()
    {
        var gets = typeof(ApplicationsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<HttpGetAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(gets);
    }

    [Fact]
    public void EveryAgentActionOnApplicationsController_RequiresAgentIdentity()
    {
        var ungated = typeof(ApplicationsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => a is HttpMethodAttribute))
            .Where(m => m.GetCustomAttribute<RequireAgentIdentityAttribute>() is null)
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(ungated);
    }

    /// <summary>Enrollment is the one agent route with neither a certificate nor a session in front
    /// of it, so it is the one route with a rate limit — and the only one, so nothing else slows.</summary>
    [Fact]
    public void Enroll_IsTheOnlyRateLimitedAction()
    {
        var limited = typeof(AgentPackagesController).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<EnableRateLimitingAttribute>() is not null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        Assert.Equal(new[] { $"{nameof(HostsController)}.{nameof(HostsController.Enroll)}" }, limited);
        Assert.Equal(
            EnrollmentRateLimit.PolicyName,
            typeof(HostsController).GetMethod(nameof(HostsController.Enroll))!.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("  ", "unknown")]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void EnrollmentRateLimit_PartitionsByAddress_WithOneBucketForTheUnknown(string? address, string expected)
    {
        Assert.Equal(expected, EnrollmentRateLimit.PartitionKey(address));
    }

    [Fact]
    public void RequestPresentedAVerifiedAgentCertificate_ExactSuccess_ReturnsTrue()
    {
        Assert.True(AgentPackagesController.RequestPresentedAVerifiedAgentCertificate("SUCCESS"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NONE")]
    [InlineData("FAILED:self signed certificate")]
    [InlineData("success")]
    public void RequestPresentedAVerifiedAgentCertificate_AnythingElse_ReturnsFalse(string? headerValue)
    {
        Assert.False(AgentPackagesController.RequestPresentedAVerifiedAgentCertificate(headerValue));
    }
}
