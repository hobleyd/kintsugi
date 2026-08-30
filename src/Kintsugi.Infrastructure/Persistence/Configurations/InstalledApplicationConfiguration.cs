using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class InstalledApplicationConfiguration : IEntityTypeConfiguration<InstalledApplication>
{
    public void Configure(EntityTypeBuilder<InstalledApplication> builder)
    {
        builder.ToTable("installed_applications");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Name).HasMaxLength(255).IsRequired();
        builder.Property(a => a.Version).HasMaxLength(64).IsRequired();
        builder.Property(a => a.ApplicationIdentifier).HasMaxLength(255);

        builder.HasIndex(a => a.Name);
        builder.HasIndex(a => a.ParentApplicationId);
        builder.HasIndex(a => new { a.HostId, a.Name, a.Version }).IsUnique();

        builder.HasOne<Host>()
            .WithMany()
            .HasForeignKey(a => a.HostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<InstalledApplication>()
            .WithMany()
            .HasForeignKey(a => a.ParentApplicationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
