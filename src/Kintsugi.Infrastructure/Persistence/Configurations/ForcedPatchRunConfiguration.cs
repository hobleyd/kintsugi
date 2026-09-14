using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class ForcedPatchRunConfiguration : IEntityTypeConfiguration<ForcedPatchRun>
{
    public void Configure(EntityTypeBuilder<ForcedPatchRun> builder)
    {
        builder.ToTable("forced_patch_runs");

        builder.HasKey(r => r.Id);

        // Same widths as patch_failures' equivalents, because they hold the same two strings: an
        // application name as the inventory reports it, and an UpgradePath platform bucket.
        builder.Property(r => r.ApplicationName).HasMaxLength(255).IsRequired();
        builder.Property(r => r.Platform).HasMaxLength(64).IsRequired();

        builder.Property(r => r.RequestedUtc).IsRequired();
        builder.Property(r => r.ExpiresUtc).IsRequired();

        // What an agent's poll reads: everything still collectable for one host. Narrow and hit
        // once a minute per logged-in host, so it is worth being an index rather than a scan.
        builder.HasIndex(r => new { r.HostId, r.CollectedUtc, r.ExpiresUtc });

        // What RequestForcedPatchRunsCommandHandler looks an outstanding row up by, so a second
        // press renews rather than duplicating — see ForcedPatchRun.Renew.
        builder.HasIndex(r => new { r.HostId, r.ApplicationName, r.Platform });

        builder.HasOne<Host>()
            .WithMany()
            .HasForeignKey(r => r.HostId)
            // A removed host's instructions go with it: there is no machine left to carry them out
            // — the same reasoning as PatchFailureConfiguration's cascade.
            .OnDelete(DeleteBehavior.Cascade);
    }
}
