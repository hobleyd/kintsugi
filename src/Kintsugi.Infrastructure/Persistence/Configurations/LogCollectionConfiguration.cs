using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class LogCollectionConfiguration : IEntityTypeConfiguration<LogCollection>
{
    public void Configure(EntityTypeBuilder<LogCollection> builder)
    {
        builder.ToTable("log_collections");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.RequestedBy).HasMaxLength(LogCollection.MaxRequestedByLength).IsRequired();
        builder.Property(c => c.Detail).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.FailureReason).HasMaxLength(LogCollection.MaxFailureReasonLength);
        builder.Property(c => c.Content);

        // What an agent's check-in and the page's polling read: the newest collection of one host.
        builder.HasIndex(c => new { c.HostId, c.RequestedUtc });

        builder.HasOne<Host>()
            .WithMany()
            .HasForeignKey(c => c.HostId)
            // A removed host's logs go with it.
            .OnDelete(DeleteBehavior.Cascade);
    }
}
