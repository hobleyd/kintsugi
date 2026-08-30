using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class PatchConfiguration : IEntityTypeConfiguration<Patch>
{
    public void Configure(EntityTypeBuilder<Patch> builder)
    {
        builder.ToTable("patches");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).HasMaxLength(255).IsRequired();
        builder.Property(p => p.Vendor).HasMaxLength(255).IsRequired();
        builder.Property(p => p.Version).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Severity).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.Description).HasMaxLength(2000);

        builder.HasIndex(p => new { p.Vendor, p.Name, p.Version }).IsUnique();
    }
}
