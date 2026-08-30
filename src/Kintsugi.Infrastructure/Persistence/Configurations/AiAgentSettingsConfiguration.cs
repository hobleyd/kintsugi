using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class AiAgentSettingsConfiguration : IEntityTypeConfiguration<AiAgentSettings>
{
    public void Configure(EntityTypeBuilder<AiAgentSettings> builder)
    {
        builder.ToTable("ai_agent_settings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.ApiKey).HasMaxLength(512);
        builder.Property(s => s.BaseUrl).HasMaxLength(512);
        builder.Property(s => s.Model).HasMaxLength(128);
        builder.Property(s => s.IsEnabled).IsRequired();
    }
}
