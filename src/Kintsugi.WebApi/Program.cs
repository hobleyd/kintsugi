using HealthChecks.NpgSql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
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

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddRazorPages();

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

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseSwagger(options => options.RouteTemplate = "swagger/{documentName}/swagger.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Kintsugi API v1");
    options.RoutePrefix = "swagger";
});

app.UseStaticFiles();

app.UseAuthentication();

// Gates the browser UI behind Authentication settings. Scoped to everything except /api (agents
// authenticate via mTLS + RequireAgentIdentity, not cookies — see RequireAgentIdentityAttribute),
// /swagger, and /health.
var alwaysExemptPrefixes = new[] { "/api", "/swagger", "/health" };
app.Use(async (context, next) =>
{
    if (alwaysExemptPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
    {
        await next();
        return;
    }

    var authSettingsRepository = context.RequestServices.GetRequiredService<IAuthenticationSettingsRepository>();
    var authSettings = await authSettingsRepository.GetAsync(context.RequestAborted);

    if (authSettings is null)
    {
        // Nothing has been saved on the Authentication settings page yet — there's no way to sign
        // in and no admin has decided whether to require it, so lock everything else down to that
        // page rather than leaving the whole site open by default.
        if (!context.Request.Path.StartsWithSegments("/settings/authentication"))
        {
            context.Response.Redirect("/settings/authentication");
            return;
        }
    }
    else if (authSettings.IsEnabled
        && !context.Request.Path.StartsWithSegments("/account")
        && context.User.Identity?.IsAuthenticated != true)
    {
        var returnUrl = context.Request.Path + context.Request.QueryString;
        context.Response.Redirect($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return;
    }

    await next();
});

app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/hosts"));

app.MapControllers();
app.MapRazorPages();
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
