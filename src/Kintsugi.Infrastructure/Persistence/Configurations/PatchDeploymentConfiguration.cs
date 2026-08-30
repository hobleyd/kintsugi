using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class PatchDeploymentConfiguration : IEntityTypeConfiguration<PatchDeployment>
{
    public void Configure(EntityTypeBuilder<PatchDeployment> builder)
    {
        builder.ToTable("patch_deployments");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(d => d.FailureReason).HasMaxLength(2000);

        builder.HasIndex(d => d.HostId);
        builder.HasIndex(d => d.PatchId);
    }
}
