using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class PackageAssessmentConfiguration : IEntityTypeConfiguration<PackageAssessment>
{
    public void Configure(EntityTypeBuilder<PackageAssessment> builder)
    {
        builder.ToTable("package_assessments");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Ecosystem).IsRequired().HasMaxLength(64);
        builder.Property(a => a.Name).IsRequired().HasMaxLength(255);
        builder.Property(a => a.Version).IsRequired().HasMaxLength(128);
        builder.Property(a => a.LastError).HasMaxLength(1024);

        // The identity of an assessment. One answer serves every host running that triple, which
        // is what keeps a hundred identical Ubuntu machines to one set of queries.
        builder.HasIndex(a => new { a.Ecosystem, a.Name, a.Version }).IsUnique();

        // The work queue's ordering key — least recently assessed first, nulls ahead of
        // everything, so a partial run resumes where the last one stopped.
        builder.HasIndex(a => a.LastAssessedUtc);
    }
}
