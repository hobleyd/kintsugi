using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class VantaSettingsConfiguration : IEntityTypeConfiguration<VantaSettings>
{
    public void Configure(EntityTypeBuilder<VantaSettings> builder)
    {
        builder.ToTable("vanta_settings");

        builder.HasKey(s => s.Id);

        // 512 to match AuthenticationSettings.ClientSecret and GitHubSettings' tokens.
        builder.Property(s => s.ClientId).HasMaxLength(512);
        builder.Property(s => s.ClientSecret).HasMaxLength(512);
        builder.Property(s => s.ApiBaseUrl).HasMaxLength(255);
        builder.Property(s => s.ConsoleBaseUrl).HasMaxLength(255);
        builder.Property(s => s.VulnerableComponentResourceId).HasMaxLength(255);
        builder.Property(s => s.PackageVulnerabilityResourceId).HasMaxLength(255);
    }
}
