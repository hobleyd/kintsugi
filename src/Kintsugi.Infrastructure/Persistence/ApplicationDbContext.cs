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
    public DbSet<PatchFailure> PatchFailures => Set<PatchFailure>();
    public DbSet<InstalledApplication> InstalledApplications => Set<InstalledApplication>();
    public DbSet<AiAgentSettings> AiAgentSettings => Set<AiAgentSettings>();
    public DbSet<UpgradePath> UpgradePaths => Set<UpgradePath>();
    public DbSet<PatchingPolicySettings> PatchingPolicySettings => Set<PatchingPolicySettings>();
    public DbSet<AgentPackage> AgentPackages => Set<AgentPackage>();
    public DbSet<AuthenticationSettings> AuthenticationSettings => Set<AuthenticationSettings>();
    public DbSet<AuditSettings> AuditSettings => Set<AuditSettings>();
    public DbSet<ApprovedScript> ApprovedScripts => Set<ApprovedScript>();
    public DbSet<GitHubSettings> GitHubSettings => Set<GitHubSettings>();
    public DbSet<VantaSettings> VantaSettings => Set<VantaSettings>();
    public DbSet<RemoteControlSession> RemoteControlSessions => Set<RemoteControlSession>();
    public DbSet<VulnerabilitySettings> VulnerabilitySettings => Set<VulnerabilitySettings>();
    public DbSet<CpeMapping> CpeMappings => Set<CpeMapping>();
    public DbSet<Vulnerability> Vulnerabilities => Set<Vulnerability>();
    public DbSet<CpeAssessment> CpeAssessments => Set<CpeAssessment>();
    public DbSet<VulnerabilityMatch> VulnerabilityMatches => Set<VulnerabilityMatch>();
    // Linux operating-system packages, deliberately their own table rather than a flag on
    // InstalledApplications — see InstalledPackage on why that separation is the design.
    public DbSet<InstalledPackage> InstalledPackages => Set<InstalledPackage>();
    public DbSet<PackageAssessment> PackageAssessments => Set<PackageAssessment>();
    public DbSet<PackageVulnerabilityMatch> PackageVulnerabilityMatches => Set<PackageVulnerabilityMatch>();
    public DbSet<OsvAdvisory> OsvAdvisories => Set<OsvAdvisory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("patching");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
