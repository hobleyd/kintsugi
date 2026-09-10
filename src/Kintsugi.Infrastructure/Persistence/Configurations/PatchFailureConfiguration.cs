using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class PatchFailureConfiguration : IEntityTypeConfiguration<PatchFailure>
{
    public void Configure(EntityTypeBuilder<PatchFailure> builder)
    {
        builder.ToTable("patch_failures");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.ApplicationName).HasMaxLength(255).IsRequired();
        builder.Property(f => f.Platform).HasMaxLength(64);
        builder.Property(f => f.InstalledVersion).HasMaxLength(64);
        builder.Property(f => f.AttemptedVersion).HasMaxLength(64);

        // Wide, and deliberately the widest thing this schema stores: the value is a failing
        // script's captured output, which is the only evidence anybody has to work from. The agents
        // truncate to a quarter of this before sending (see each one's
        // `upgrade::MAX_REPORTED_FAILURE_BYTES`) and ReportPatchFailureCommandValidator rejects
        // anything longer than the column — a validator ceiling *below* what an agent sends would
        // 400 the report and lose the failure silently, which is the exact class of problem this
        // feature exists to surface.
        builder.Property(f => f.Details).HasMaxLength(16000).IsRequired();

        builder.HasIndex(f => f.HostId);

        // What ReportPatchFailureCommandHandler looks a repeat up by, and the ordering the Failed
        // Updates screen reads in.
        builder.HasIndex(f => new { f.HostId, f.ApplicationName });
        builder.HasIndex(f => new { f.Resolution, f.LastFailedUtc });

        builder.HasOne<Host>()
            .WithMany()
            .HasForeignKey(f => f.HostId)
            // A removed host's failures go with it. They name a machine that no longer exists and
            // there is nothing left to fix on it — see ConfirmHostRemovalCommandHandler.
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(f => f.Resolution)
            .HasDefaultValue(PatchFailureResolution.Outstanding);
    }
}
