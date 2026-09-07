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

        // Nullable, and stays that way: a package published by a release script has no upstream at
        // all, and rows imported before these columns existed carry no pin until a refresh
        // backfills one. See AgentPackage.UpstreamSha256.
        builder.Property(p => p.UpstreamSha256).HasMaxLength(64);
        builder.Property(p => p.UpstreamDownloadUrl).HasMaxLength(1024);

        builder.HasIndex(p => new { p.Platform, p.Version }).IsUnique();
    }
}
