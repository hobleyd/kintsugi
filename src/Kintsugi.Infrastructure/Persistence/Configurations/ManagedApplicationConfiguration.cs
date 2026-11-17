using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class ManagedApplicationConfiguration : IEntityTypeConfiguration<ManagedApplication>
{
    public void Configure(EntityTypeBuilder<ManagedApplication> builder)
    {
        builder.ToTable("managed_applications");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.ApplicationName).HasMaxLength(ManagedApplication.MaxNameLength).IsRequired();
        builder.Property(a => a.Owner).HasMaxLength(ManagedApplication.MaxOwnerLength).IsRequired();

        builder.Property(a => a.NormalizedName).HasMaxLength(ManagedApplication.MaxNameLength).IsRequired();

        // One row per application whatever its capitals — "Falcon" and "falcon" are the same product.
        // The command looks a name up before it writes; this is what holds when two requests race.
        builder.HasIndex(a => a.NormalizedName).IsUnique();
    }
}
