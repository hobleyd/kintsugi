using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class HostBaselineItemStateConfiguration : IEntityTypeConfiguration<HostBaselineItemState>
{
    public void Configure(EntityTypeBuilder<HostBaselineItemState> builder)
    {
        builder.ToTable("host_baseline_item_states");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.ItemKey).HasMaxLength(100).IsRequired();
        builder.Property(s => s.ObservedValue).HasMaxLength(HostBaselineItemState.MaxObservedLength);
        builder.Property(s => s.Detail).HasMaxLength(HostBaselineItemState.MaxDetailLength);
        // One row per (host, version, item): what ReportBaselineResultsCommandHandler upserts.
        builder.HasIndex(s => new { s.HostId, s.BaselineVersionId, s.ItemKey }).IsUnique();
        // What every compliance count reads: all hosts' rows for one version.
        builder.HasIndex(s => s.BaselineVersionId);
        builder.HasOne<Host>().WithMany().HasForeignKey(s => s.HostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<BaselineVersion>().WithMany().HasForeignKey(s => s.BaselineVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}
