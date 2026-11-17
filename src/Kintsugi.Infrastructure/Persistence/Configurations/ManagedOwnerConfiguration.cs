using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class ManagedOwnerConfiguration : IEntityTypeConfiguration<ManagedOwner>
{
    public void Configure(EntityTypeBuilder<ManagedOwner> builder)
    {
        builder.ToTable("managed_owners");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Name).HasMaxLength(ManagedApplication.MaxOwnerLength).IsRequired();

        builder.Property(o => o.NormalizedName).HasMaxLength(ManagedApplication.MaxOwnerLength).IsRequired();

        builder.HasIndex(o => o.NormalizedName).IsUnique();
    }
}
