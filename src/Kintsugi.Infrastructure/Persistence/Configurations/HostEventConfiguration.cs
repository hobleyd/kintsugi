using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class HostEventConfiguration : IEntityTypeConfiguration<HostEvent>
{
    public void Configure(EntityTypeBuilder<HostEvent> builder)
    {
        builder.ToTable("host_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(e => e.Subject).HasMaxLength(255).IsRequired();
        builder.Property(e => e.FromVersion).HasMaxLength(255);
        builder.Property(e => e.ToVersion).HasMaxLength(255);
        builder.Property(e => e.Actor).HasMaxLength(320);
        builder.Property(e => e.Detail).HasMaxLength(HostEvent.MaxDetailLength + 64);
        builder.Property(e => e.OccurredUtc).IsRequired();
        builder.Property(e => e.Count).IsRequired();

        // One host's timeline, newest first, and the latest event for a subject when a repeated
        // failure is folded.
        builder.HasIndex(e => new { e.HostId, e.OccurredUtc });

        // What the retention sweep deletes by.
        builder.HasIndex(e => e.OccurredUtc);

        builder.HasOne<Host>()
            .WithMany()
            .HasForeignKey(e => e.HostId)
            // A removed host's history goes with it, like its failures and forced runs.
            .OnDelete(DeleteBehavior.Cascade);
    }
}
