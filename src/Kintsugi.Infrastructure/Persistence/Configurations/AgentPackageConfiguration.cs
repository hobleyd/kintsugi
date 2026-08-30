using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class AgentPackageConfiguration : IEntityTypeConfiguration<AgentPackage>
{
    public void Configure(EntityTypeBuilder<AgentPackage> builder)
    {
        builder.ToTable("agent_packages");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Platform).HasMaxLength(32).IsRequired();
        builder.Property(p => p.Version).HasMaxLength(64).IsRequired();
        builder.Property(p => p.FileName).HasMaxLength(255).IsRequired();
        builder.Property(p => p.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Sha256Signature).HasMaxLength(256).IsRequired();
        builder.Property(p => p.ReleaseNotes).HasMaxLength(2000);

        builder.HasIndex(p => new { p.Platform, p.Version }).IsUnique();
    }
}
