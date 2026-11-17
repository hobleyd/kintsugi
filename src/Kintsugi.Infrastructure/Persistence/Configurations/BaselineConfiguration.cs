using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class BaselineConfiguration : IEntityTypeConfiguration<Baseline>
{
    public void Configure(EntityTypeBuilder<Baseline> builder)
    {
        builder.ToTable("baselines");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Name).HasMaxLength(Baseline.MaxNameLength).IsRequired();
        builder.HasIndex(b => b.Name).IsUnique();
        builder.Property(b => b.CreatedBy).HasMaxLength(255);
        builder.Property(b => b.AssignedBy).HasMaxLength(255);
        // A uuid[] column (Npgsql's mapping for a primitive collection). Ids rather than a join table
        // because nothing queries from the host side yet; a removed host's id simply stops matching.
        builder.Property(b => b.AssignedHostIds);
    }
}
