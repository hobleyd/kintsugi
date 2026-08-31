using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Infrastructure.AgentPackages;
using Kintsugi.Infrastructure.Ai;
using Kintsugi.Infrastructure.CheckIn;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;
using Kintsugi.Infrastructure.Security;
using Kintsugi.Infrastructure.Storage;

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
        services.AddScoped<IAuthenticationSettingsRepository, AuthenticationSettingsRepository>();
        services.AddSingleton<IAgentPackageStorage, AgentPackageFileStorage>();
        services.AddSingleton<IAgentPackageArchiveRewriter, AgentPackageArchiveRewriter>();
        // The upstream client builds come from — see GitHubAgentPackageSourceClient and the
        // Clients page's "Refresh clients" button.
        services.AddHttpClient<IAgentPackageSourceClient, GitHubAgentPackageSourceClient>();
        services.AddHttpClient<IOllamaModelsClient, OllamaModelsClient>();
        services.AddHttpClient<IUpgradePathResearchClient, AiUpgradePathResearchClient>();
        services.AddScoped<IGooseCliClient, GooseCliClient>();
        services.AddSingleton<ICaService, CaService>();
        services.AddSingleton<IArtifactSigningService, ArtifactSigningService>();
        services.AddSingleton<IAgentEnrollmentOptions, AgentEnrollmentOptions>();
        services.AddSingleton<ICheckInLoadBalancer, CheckInLoadBalancer>();

        return services;
    }
}
