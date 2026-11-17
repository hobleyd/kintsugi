using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class KpiSnapshotConfiguration : IEntityTypeConfiguration<KpiSnapshot>
{
    public void Configure(EntityTypeBuilder<KpiSnapshot> builder)
    {
        builder.ToTable("kpi_snapshots");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.KpiId).HasMaxLength(64).IsRequired();
        builder.Property(s => s.SnapshotDateUtc).IsRequired();

        // One value per KPI per day: a second run on the same day cannot write twice.
        builder.HasIndex(s => new { s.SnapshotDateUtc, s.KpiId }).IsUnique();
    }
}
