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
        services.AddScoped<IVantaSettingsRepository, VantaSettingsRepository>();
        services.AddScoped<IRemoteControlSessionRepository, RemoteControlSessionRepository>();
        // Scoped and read per call, for the same reason IGitHubSettingsProvider is: these values are
        // edited on a settings page while the process runs.
        services.AddScoped<IVantaSettingsProvider, VantaSettingsProvider>();
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
        // The Vanta sync. Its access token is held by a *singleton* alongside the typed client
        // rather than inside it, because Vanta permits one active token per application and revokes
        // the previous one whenever a new one is issued — two components each holding their own
        // would spend a sync invalidating each other. See VantaAccessTokenProvider.
        services.AddHttpClient<IVantaSyncClient, VantaSyncClient>();
        services.AddSingleton<VantaAccessTokenProvider>();
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
