using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Infrastructure.AgentPackages;
using Kintsugi.Infrastructure.ScriptApproval;
using Kintsugi.Infrastructure.Ai;
using Kintsugi.Infrastructure.CheckIn;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;
using Kintsugi.Infrastructure.Security;
using Kintsugi.Infrastructure.Storage;
using Kintsugi.Infrastructure.Vanta;
using Kintsugi.Infrastructure.Vulnerabilities;

namespace Kintsugi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("Connection string 'Database' was not found.");

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "patching")));

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<IHostRepository, HostRepository>();
        services.AddScoped<IPatchRepository, PatchRepository>();
        services.AddScoped<IPatchDeploymentRepository, PatchDeploymentRepository>();
        services.AddScoped<IPatchFailureRepository, PatchFailureRepository>();
        services.AddScoped<IInstalledApplicationRepository, InstalledApplicationRepository>();
        services.AddScoped<IAiAgentSettingsRepository, AiAgentSettingsRepository>();
        services.AddScoped<IUpgradePathRepository, UpgradePathRepository>();
        services.AddScoped<IPatchingPolicySettingsRepository, PatchingPolicySettingsRepository>();
        services.AddScoped<IAgentPackageRepository, AgentPackageRepository>();
        services.AddScoped<IApprovedScriptRepository, ApprovedScriptRepository>();
        services.AddScoped<IGitHubSettingsRepository, GitHubSettingsRepository>();
        // Scoped, and read per call by every GitHub client — see GitHubSettings for why none of them
        // may capture these values in a constructor any more.
        services.AddScoped<IGitHubSettingsProvider, GitHubSettingsProvider>();
        services.AddScoped<IAuthenticationSettingsRepository, AuthenticationSettingsRepository>();
        services.AddScoped<IAuditSettingsRepository, AuditSettingsRepository>();
        services.AddScoped<IVantaSettingsRepository, VantaSettingsRepository>();
        services.AddScoped<IRemoteControlSessionRepository, RemoteControlSessionRepository>();
        // Scoped and read per call, for the same reason IGitHubSettingsProvider is: these values are
        // edited on a settings page while the process runs.
        services.AddScoped<IVantaSettingsProvider, VantaSettingsProvider>();
        services.AddScoped<IVulnerabilitySettingsRepository, VulnerabilitySettingsRepository>();
        services.AddScoped<IVulnerabilitySettingsProvider, VulnerabilitySettingsProvider>();
        services.AddScoped<IVulnerabilityRepository, VulnerabilityRepository>();
        services.AddSingleton<IAgentPackageStorage, AgentPackageFileStorage>();
        services.AddSingleton<IAgentPackageArchiveRewriter, AgentPackageArchiveRewriter>();
        // The upstream client builds come from — see GitHubAgentPackageSourceClient and the
        // Clients page's "Refresh clients" button.
        services.AddHttpClient<IAgentPackageSourceClient, GitHubAgentPackageSourceClient>();
        // The two halves of the script-approval round trip. Separate HttpClients because only the
        // publisher is given the write token (see ScriptApprovalRepository.TokenConfigurationKey) —
        // the reader needs no credential at all for a public repository, and sharing one client
        // would hand it the write scope for nothing.
        services.AddHttpClient<IScriptApprovalSourceClient, GitHubScriptApprovalSourceClient>();
        services.AddHttpClient<IScriptApprovalPublisher, GitHubScriptApprovalPublisher>();
        services.AddHttpClient<IOllamaModelsClient, OllamaModelsClient>();
        services.AddHttpClient<IUpgradePathResearchClient, AiUpgradePathResearchClient>();
        // The same object under a second interface, not a second registration of the type: it is
        // where the per-provider dispatch lives, and a separate instance would mean a separate
        // HttpClient for one extra prompt. See ICpeSuggestionClient on why the concern is split
        // even though the implementation is shared.
        services.AddScoped<ICpeSuggestionClient>(sp => (AiUpgradePathResearchClient)sp.GetRequiredService<IUpgradePathResearchClient>());
        // The Vanta sync. Its access token is held by a *singleton* alongside the typed client
        // rather than inside it, because Vanta permits one active token per application and revokes
        // the previous one whenever a new one is issued — two components each holding their own
        // would spend a sync invalidating each other. See VantaAccessTokenProvider.
        services.AddHttpClient<IVantaSyncClient, VantaSyncClient>();
        services.AddSingleton<VantaAccessTokenProvider>();
        // CISA's KEV catalogue: one public JSON document, no key, no paging.
        services.AddHttpClient<IKevCatalogClient, KevCatalogClient>();
        // The NVD 2.0 API. Its rate limiter is a *singleton* beside the typed client, for the same
        // shape of reason VantaAccessTokenProvider is one: NVD counts requests per source address
        // over a rolling window, so the allowance belongs to the server rather than to a scope.
        // Two components each keeping their own tally would each stay under the limit and together
        // sail past it — and NVD answers that with 403s that read like an authentication failure.
        services.AddHttpClient<INvdClient, NvdClient>();
        services.AddSingleton<NvdRateLimiter>();
        services.AddScoped<IGooseCliClient, GooseCliClient>();
        services.AddScoped<IClaudeAgentSdkClient, ClaudeAgentSdkClient>();
        services.AddSingleton<ICaService, CaService>();
        services.AddSingleton<IArtifactSigningService, ArtifactSigningService>();
        services.AddSingleton<IScriptSignatureVerifier, ScriptSignatureVerifier>();
        services.AddSingleton<IAgentEnrollmentOptions, AgentEnrollmentOptions>();
        services.AddSingleton<IAgentApiOptions, AgentApiOptions>();
        services.AddSingleton<ICheckInLoadBalancer, CheckInLoadBalancer>();

        return services;
    }
}
