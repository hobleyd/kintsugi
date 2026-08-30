using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class AuthenticationSettingsConfiguration : IEntityTypeConfiguration<AuthenticationSettings>
{
    public void Configure(EntityTypeBuilder<AuthenticationSettings> builder)
    {
        builder.ToTable("authentication_settings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.ClientId).HasMaxLength(256);
        builder.Property(s => s.ClientSecret).HasMaxLength(512);
        builder.Property(s => s.Authority).HasMaxLength(512);
        builder.Property(s => s.TenantId).HasMaxLength(128);
        builder.Property(s => s.HostedDomain).HasMaxLength(255);
        builder.Property(s => s.IsEnabled).IsRequired();
    }
}
