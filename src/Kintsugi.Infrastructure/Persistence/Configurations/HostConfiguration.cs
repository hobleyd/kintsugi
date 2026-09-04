using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class HostConfiguration : IEntityTypeConfiguration<Host>
{
    public void Configure(EntityTypeBuilder<Host> builder)
    {
        builder.ToTable("hosts");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.Hostname).HasMaxLength(255).IsRequired();
        builder.Property(h => h.SerialNumber).HasMaxLength(128).IsRequired();
        builder.Property(h => h.OperatingSystem).HasMaxLength(255);
        builder.Property(h => h.IpAddress).HasMaxLength(45);
        builder.Property(h => h.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(h => h.OperatingSystemLatestVersion).HasMaxLength(64);
        builder.Property(h => h.AgentVersion).HasMaxLength(64);

        builder.HasIndex(h => h.Hostname).IsUnique();
        builder.HasIndex(h => h.SerialNumber).IsUnique();
    }
}
