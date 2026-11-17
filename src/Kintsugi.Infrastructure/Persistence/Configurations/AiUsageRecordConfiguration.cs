using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class AiUsageRecordConfiguration : IEntityTypeConfiguration<AiUsageRecord>
{
    public void Configure(EntityTypeBuilder<AiUsageRecord> builder)
    {
        builder.ToTable("ai_usage");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.ConnectionName).HasMaxLength(128).IsRequired();
        builder.Property(r => r.Model).HasMaxLength(128).IsRequired();
        builder.Property(r => r.Subject).HasMaxLength(255);

        // Six decimal places: a single call on a small model costs a fraction of a cent, and a month
        // of them is summed, so rounding each to cents would lose the total.
        builder.Property(r => r.EstimatedCostUsd).HasPrecision(18, 6);

        // The budget guard sums everything since the start of the month before every AI call.
        builder.HasIndex(r => r.OccurredUtc);
    }
}
