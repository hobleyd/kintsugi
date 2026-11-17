using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class HostActivitySettingsConfiguration : IEntityTypeConfiguration<HostActivitySettings>
{
    public void Configure(EntityTypeBuilder<HostActivitySettings> builder)
    {
        builder.ToTable("host_activity_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.RetentionMonths).IsRequired();
    }
}
