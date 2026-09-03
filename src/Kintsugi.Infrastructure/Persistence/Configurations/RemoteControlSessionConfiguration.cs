using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class RemoteControlSessionConfiguration : IEntityTypeConfiguration<RemoteControlSession>
{
    public void Configure(EntityTypeBuilder<RemoteControlSession> builder)
    {
        builder.ToTable("remote_control_sessions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.SerialNumber).HasMaxLength(128).IsRequired();
        builder.Property(s => s.Hostname).HasMaxLength(255).IsRequired();
        builder.Property(s => s.RequestedBy).HasMaxLength(320).IsRequired();
        builder.Property(s => s.Consent).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.EndReason).HasMaxLength(256);

        // No navigation to Host and deliberately no foreign key, so a removed host cannot take its
        // audit trail with it — see the note on the entity. HostId is indexed anyway, since "who
        // has connected to this machine" is the question this table gets asked.
        builder.HasIndex(s => s.HostId);
        builder.HasIndex(s => s.SerialNumber);
        builder.HasIndex(s => s.CreatedAtUtc);
    }
}
