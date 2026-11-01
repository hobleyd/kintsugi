using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// Fails when an endpoint is added with no gate at all. Nothing else does: nginx's client-certificate
/// regex is an exact match that never covers a browser-driven route, and <c>Program.cs</c> exempts
/// all of <c>/api</c> from the sign-in gate, so an endpoint that carries neither
/// <see cref="RequireAdminSessionAttribute"/> nor <see cref="RequireAgentIdentityAttribute"/> is
/// anonymous. The exceptions below are the deliberate ones; adding to the list is the review
/// conversation this test exists to force.
/// </summary>
/// <remarks>
/// This reads the route table the running app actually serves, from <see cref="EndpointDataSource"/>,
/// rather than reflecting over controller types. A reflection scan only finds what it was written to
/// look for — an <c>[HttpGet]</c>-style attribute on a <c>ControllerBase</c> subclass — and MVC
/// routes more than that: <c>[AcceptVerbs]</c>, a plain public method under a class-level
/// <c>[Route]</c>, inherited actions, controllers that are not <c>ControllerBase</c>, and anything
/// mapped in <c>Program.cs</c> such as <c>/health</c>. An endpoint's metadata carries the attributes
/// of its action and its controller class, inherited ones included, so the gate is read from there.
///
/// <para>
/// To count another attribute as a gate, add it to <see cref="IsGated"/>. A
/// <c>[RequireAgentCertificate]</c> belongs there once it exists; it is not in this branch.
/// </para>
/// </remarks>
public class ControllerRouteGateTests : IClassFixture<ControllerRouteGateTests.RouteTableFactory>
{
    /// <summary>
    /// Keyed <c>Controller.Action</c> for an MVC action and by route pattern for anything else, so
    /// an entry reads the same way as the endpoint it excuses.
    /// </summary>
    private static readonly HashSet<string> DeliberatelyAnonymous = new()
    {
        // An unenrolled agent has no certificate yet, so enrolment cannot require one.
        "HostsController.Enroll",
        // Public package metadata and the archive itself (CI publishes the same bytes as a GitHub
        // release); Download decides for itself whether to write the enrolment token in.
        "AgentPackagesController.GetAll",
        "AgentPackagesController.GetLatest",
        "AgentPackagesController.Download",
        // Behind nginx's agent-certificate regex; see PatchingPolicyController's remarks.
        "PatchingPolicyController.Get",
        // The routes that decide whether and how to sign in, so they cannot require being signed in.
        "SessionController.Get",
        "SessionController.Challenge",
        "SessionController.Logout",
        // The container health check and nginx's upstream probe: they must answer with nobody
        // signed in, and they report only whether the database is reachable.
        "/health",
    };

    private readonly RouteTableFactory _factory;

    public ControllerRouteGateTests(RouteTableFactory factory) => _factory = factory;

    [Fact]
    public void EveryEndpoint_IsGated_OrDeliberatelyAnonymous()
    {
        var ungated = Endpoints()
            .Where(e => !IsGated(e.Endpoint) && !DeliberatelyAnonymous.Contains(e.Name))
            .Select(e => $"{e.Name} [{e.Endpoint.DisplayName}]")
            .ToList();

        Assert.True(
            ungated.Count == 0,
            "Endpoints with no [RequireAdminSession] or [RequireAgentIdentity]: " + string.Join(", ", ungated));
    }

    [Fact]
    public void DeliberateExceptions_AllStillMatchARealEndpoint()
    {
        var real = Endpoints().Select(e => e.Name).ToHashSet();

        var stale = DeliberatelyAnonymous.Where(name => !real.Contains(name)).ToList();

        Assert.True(
            stale.Count == 0,
            "Exceptions that match no endpoint the app serves (remove them, or fix the name): " + string.Join(", ", stale));
    }

    [Fact]
    public void RouteTable_IsNotEmpty()
    {
        // A host that failed to map its controllers would make both tests above pass vacuously.
        Assert.Contains(Endpoints(), e => e.Name == "SessionController.Get");
        Assert.Contains(Endpoints(), e => e.Name == "/health");
    }

    private IEnumerable<(string Name, Endpoint Endpoint)> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.Select(endpoint =>
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            var name = action is not null
                ? $"{action.ControllerTypeInfo.Name}.{action.MethodInfo.Name}"
                : (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint.DisplayName ?? "(unnamed endpoint)";
            return (name, endpoint);
        });

    private static bool IsGated(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<RequireAdminSessionAttribute>() is not null
        || endpoint.Metadata.GetMetadata<RequireAgentIdentityAttribute>() is not null;

    /// <summary>
    /// The real application, minus what needs the container. The "Testing" environment skips the
    /// database migration, the CA and the GitHub seed (see <c>Program.cs</c>); here the database
    /// becomes an in-memory one, the data-protection key ring moves from <c>/data</c> to a temp
    /// folder, and the background services are removed so nothing reaches for a database that is
    /// not there. These overrides live in the test, not in <c>Program.cs</c>, so the production
    /// project takes no dependency on the in-memory provider.
    /// </summary>
    public sealed class RouteTableFactory : WebApplicationFactory<Program>
    {
        private readonly string _keyRing = Path.Combine(Path.GetTempPath(), "kintsugi-route-gate-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Program.cs and AddInfrastructure both insist on this at registration time; compose
            // supplies it from the environment. Never opened: the DbContext below is in-memory and
            // the health check is only mapped, not called.
            builder.UseSetting("ConnectionStrings:Database", "Host=unused;Database=unused");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase("route-gate"));

                services.Configure<KeyManagementOptions>(options =>
                    options.XmlRepository = new FileSystemXmlRepository(Directory.CreateDirectory(_keyRing), NullLoggerFactory.Instance));

                foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                {
                    services.Remove(hosted);
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_keyRing))
            {
                Directory.Delete(_keyRing, recursive: true);
            }
        }
    }
}
