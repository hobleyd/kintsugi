using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class PatchingPolicySettingsConfiguration : IEntityTypeConfiguration<PatchingPolicySettings>
{
    public void Configure(EntityTypeBuilder<PatchingPolicySettings> builder)
    {
        builder.ToTable("patching_policy_settings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.IntervalValue).IsRequired();
        builder.Property(s => s.IntervalUnit).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(s => s.DelayValue).IsRequired();
        builder.Property(s => s.DelayUnit).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(s => s.MaxDelayCount).IsRequired();
    }
}
