using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IUnitOfWork
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<Host> Hosts => Set<Host>();
    public DbSet<Patch> Patches => Set<Patch>();
    public DbSet<PatchDeployment> PatchDeployments => Set<PatchDeployment>();
    public DbSet<InstalledApplication> InstalledApplications => Set<InstalledApplication>();
    public DbSet<AiAgentSettings> AiAgentSettings => Set<AiAgentSettings>();
    public DbSet<UpgradePath> UpgradePaths => Set<UpgradePath>();
    public DbSet<PatchingPolicySettings> PatchingPolicySettings => Set<PatchingPolicySettings>();
    public DbSet<AgentPackage> AgentPackages => Set<AgentPackage>();
    public DbSet<AuthenticationSettings> AuthenticationSettings => Set<AuthenticationSettings>();
    public DbSet<ApprovedScript> ApprovedScripts => Set<ApprovedScript>();
    public DbSet<GitHubSettings> GitHubSettings => Set<GitHubSettings>();
    public DbSet<VantaSettings> VantaSettings => Set<VantaSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("patching");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
