using HealthChecks.NpgSql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Kintsugi.Application;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Infrastructure;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.WebApi.Middleware;
using Kintsugi.WebApi.Security;
using Kintsugi.WebApi.UpgradePathScanning;

var builder = WebApplication.CreateBuilder(args);

// Everything this app says its own address is — the OIDC redirect_uri and post_logout_redirect_uri
// sent to the identity provider, and the callback URLs shown on the Authentication settings page —
// is built from Request.Scheme and Request.Host. Behind a proxy those are the proxy's, not the
// browser's, and a wrong redirect_uri is rejected by the provider outright rather than degrading:
// sign-in simply fails. So trust the forwarded pair, which nginx sends (see nginx/default.conf,
// which preserves an outer proxy's values rather than overwriting them with its own).
//
// KnownNetworks/KnownProxies are cleared rather than enumerated because the only peer that can
// reach this app is nginx, over the internal net-web network, on an address Docker assigns and
// changes. That is safe *because* the API publishes no ports (see docker-compose.yml) — nothing
// off that network can open a connection here to forge these headers. Publishing a port would
// invalidate that reasoning, and nothing here would notice.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// The identity provider (Google Workspace, Microsoft Entra, a generic OIDC provider, or Clerk) is
// configured at runtime through the Authentication settings page rather than at startup — see
// DynamicOpenIdConnectOptionsConfigurator, which loads it from the database into these options
// the first time the scheme is used (and again after a settings save invalidates the cache).
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
    .AddCookie()
    .AddOpenIdConnect();
builder.Services.ConfigureOptions<DynamicOpenIdConnectOptionsConfigurator>();

// Without this, the keys used to protect the sign-in cookie live only in the container's
// filesystem and are lost on every redeploy/restart — silently signing everyone out. Persisted
// to a volume the same way CaService's CA keypair is (see docker-compose.yml) so sessions
// actually survive a redeploy.
builder.Services.AddDataProtection()
    .SetApplicationName("Kintsugi")
    .PersistKeysToFileSystem(new DirectoryInfo("/data/dataprotection-keys"));

// The upgrade-path scanner runs in the background rather than inline with the HTTP request that
// triggers it — across hundreds of applications, a synchronous scan could take far longer than a
// request should block for. The coordinator is registered under both its own type (so the hosted
// service, which needs its writer-side methods, can depend on it directly) and the narrower
// interface (visible to Application-layer command/query handlers).
builder.Services.AddSingleton<UpgradePathScanCoordinator>();
builder.Services.AddSingleton<IUpgradePathScanCoordinator>(sp => sp.GetRequiredService<UpgradePathScanCoordinator>());
builder.Services.AddHostedService<UpgradePathScanBackgroundService>();

// Same rationale as the fleet-wide scanner above, but keyed per application: the AI provider
// behind a per-row "refresh" (e.g. Goose backed by a local model) can take far longer than a
// single HTTP request should block for.
builder.Services.AddSingleton<UpgradePathRefreshCoordinator>();
builder.Services.AddSingleton<IUpgradePathRefreshCoordinator>(sp => sp.GetRequiredService<UpgradePathRefreshCoordinator>());
builder.Services.AddHostedService<UpgradePathRefreshBackgroundService>();

// "Check for Updates" re-runs each already-resolved script's own --update-version mode — no AI
// call — so it's a separate background runner from the scanner above, with its own progress the
// UI polls independently.
builder.Services.AddSingleton<UpdateCheckCoordinator>();
builder.Services.AddSingleton<IUpdateCheckCoordinator>(sp => sp.GetRequiredService<UpdateCheckCoordinator>());
builder.Services.AddHostedService<UpdateCheckBackgroundService>();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Kintsugi API",
        Version = "v1",
        Description = "Enterprise patch management API. Manages hosts, patches, and patch deployments."
    });
    options.EnableAnnotations();

    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

var connectionString = builder.Configuration.GetConnectionString("Database");
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString ?? throw new InvalidOperationException("Connection string 'Database' was not found."), name: "postgres");

var app = builder.Build();

await ApplyMigrationsAsync(app);
await SeedGitHubSettingsFromEnvironmentAsync(app);

// Before anything that reads the scheme or host — the OIDC handler in UseAuthentication, and the
// routes that build a URL of their own: the callback URLs in GET /api/session and the fallback
// agent base URL in GET /api/admin/clients, both of which have to name the address the *browser*
// used rather than this container's.
app.UseForwardedHeaders();

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseSwagger(options => options.RouteTemplate = "swagger/{documentName}/swagger.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Kintsugi API v1");
    options.RoutePrefix = "swagger";
});

app.UseAuthentication();

// There is deliberately no redirecting sign-in gate here any more, and it is worth saying why so
// one does not come back.
//
// This app used to serve the admin UI itself as Razor Pages, so a middleware could answer a page
// request with a 302 — to /settings/authentication on a server with nothing configured, or to
// /account/login when sign-in was required and the caller had no cookie. The admin UI is now a
// Flutter web application served as static files by nginx (see nginx/default.conf and
// nginx/Dockerfile), so that page request never arrives here at all: there is nothing left under
// this app's routes but /api, /swagger, /health and the two OIDC callbacks, and redirecting any of
// those would break them — /signin-oidc in particular has to be reachable by a caller who is, by
// definition, not signed in yet.
//
// The three jobs that middleware did are now split, and all three need to stay:
//
//   * "nothing configured yet, lock everything to the authentication screen" and "sign-in required
//     and you have no cookie" are reported as data by GET /api/session, which the client fetches
//     before it renders anything and routes on. See SessionController — that route is anonymous by
//     design, and is the only new one that is.
//   * refusing the individual browser-driven /api routes is [RequireAdminSession], per controller.
//     That attribute mirrors this gate's semantics exactly (required precisely when an
//     administrator has saved AuthenticationSettings and enabled it) rather than inventing a second
//     shape that could drift from it.
//   * handing off to the identity provider is GET /api/auth/challenge, a full-page navigation the
//     client's sign-in button targets.
//
// The consequence to hold onto: /api is still exempt from any framework-level authentication here,
// so a browser-driven route added without [RequireAdminSession] is anonymous. Nothing else will
// stop it.

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

static async Task ApplyMigrationsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    const int maxAttempts = 10;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied successfully.");
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logger.LogWarning(ex, "Database not ready (attempt {Attempt}/{MaxAttempts}). Retrying in 3s...", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}

/// <summary>
/// Moves GitHub configuration out of the environment and into the database, once.
/// </summary>
/// <remarks>
/// These four values used to be read from the environment on every request. They are now edited on
/// the GitHub settings page, and the database is the only source of truth — but a deployment that
/// upgrades into this change already has them in its <c>.env</c>, and losing them silently would
/// stop agent-package refresh and script approval with no indication why. So on a server that has no
/// settings row yet, whatever the environment holds is written into one and logged; from then on the
/// environment is ignored entirely and the <c>.env</c> entries can be deleted.
///
/// Deliberately not a fallback. A row existing — even an empty one saved from the page — means the
/// environment is never consulted again, so there is never a moment where an administrator clears a
/// value on the page and an old environment variable quietly puts it back.
/// </remarks>
static async Task SeedGitHubSettingsFromEnvironmentAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var repository = scope.ServiceProvider.GetRequiredService<IGitHubSettingsRepository>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    if (await repository.GetAsync(CancellationToken.None) is not null)
    {
        return;
    }

    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var apiToken = configuration["GITHUB_API_TOKEN"];
    var agentPackageRepository = configuration["AGENT_PACKAGE_GITHUB_REPO"];
    var scriptApprovalRepository = configuration["SCRIPT_APPROVAL_GITHUB_REPO"];
    var scriptApprovalToken = configuration["SCRIPT_APPROVAL_GITHUB_TOKEN"];

    if (new[] { apiToken, agentPackageRepository, scriptApprovalRepository, scriptApprovalToken }.All(string.IsNullOrWhiteSpace))
    {
        // A fresh deploy with nothing configured either way. No row is written, so the settings page
        // opens on defaults rather than on a row of blanks — and the seed stays armed in case this
        // server is later handed an environment to migrate.
        return;
    }

    var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
    await repository.AddAsync(
        Kintsugi.Domain.Entities.GitHubSettings.Create(
            apiToken, agentPackageRepository, scriptApprovalRepository, scriptApprovalToken),
        CancellationToken.None);
    await unitOfWork.SaveChangesAsync(CancellationToken.None);

    logger.LogInformation(
        "Seeded GitHub settings from environment variables. They are now managed on the GitHub settings page, "
        + "and GITHUB_API_TOKEN / AGENT_PACKAGE_GITHUB_REPO / SCRIPT_APPROVAL_GITHUB_REPO / "
        + "SCRIPT_APPROVAL_GITHUB_TOKEN can be removed from this deployment's .env.");
}
