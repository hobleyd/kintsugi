using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class CpeAssessmentConfiguration : IEntityTypeConfiguration<CpeAssessment>
{
    public void Configure(EntityTypeBuilder<CpeAssessment> builder)
    {
        builder.ToTable("cpe_assessments");

        builder.HasKey(a => a.Id);

        // Matches InstalledApplication.Version, which is where every value here comes from.
        builder.Property(a => a.Version).IsRequired().HasMaxLength(64);
        builder.Property(a => a.LastError).HasMaxLength(1024);

        builder.HasOne<CpeMapping>()
            .WithMany()
            .HasForeignKey(a => a.CpeMappingId)
            // Re-pointing or deleting a mapping invalidates every assessment stored under it:
            // those findings describe a product the subject no longer claims to be.
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.CpeMappingId, a.Version }).IsUnique();

        // The work queue's ordering key — least recently assessed first, nulls (never assessed)
        // ahead of everything, which is how a partial run resumes where the last one stopped.
        builder.HasIndex(a => a.LastAssessedUtc);
    }
}
