using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class AuditSettingsConfiguration : IEntityTypeConfiguration<AuditSettings>
{
    public void Configure(EntityTypeBuilder<AuditSettings> builder)
    {
        builder.ToTable("audit_settings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Endpoint).HasMaxLength(2048);
        builder.Property(s => s.Region).HasMaxLength(128);
        builder.Property(s => s.ClientId).HasMaxLength(512);
        // Deliberately no maximum, unlike every other secret column (512): a Google service-account
        // key is a JSON document of a couple of kilobytes, and this column holds it whole.
        builder.Property(s => s.Secret);
        builder.Property(s => s.TenantId).HasMaxLength(128);
        builder.Property(s => s.ProjectId).HasMaxLength(128);
        builder.Property(s => s.LogGroup).HasMaxLength(512);
        builder.Property(s => s.DataCollectionRuleId).HasMaxLength(128);
        builder.Property(s => s.Stream).HasMaxLength(512);
        builder.Property(s => s.Index).HasMaxLength(128);
    }
}
