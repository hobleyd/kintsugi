using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class CpeMappingConfiguration : IEntityTypeConfiguration<CpeMapping>
{
    public void Configure(EntityTypeBuilder<CpeMapping> builder)
    {
        builder.ToTable("cpe_mappings");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.SubjectKey).IsRequired().HasMaxLength(255);
        builder.Property(m => m.DisplayName).IsRequired().HasMaxLength(255);
        builder.Property(m => m.Vendor).HasMaxLength(128);
        builder.Property(m => m.Product).HasMaxLength(128);
        builder.Property(m => m.SuggestionNotes).HasMaxLength(1024);

        // Stored as names rather than ordinals, like AiAgentSettings.Provider: the wire format is
        // an ordinal (see the enums' own remarks) but the database has no reason to be, and a name
        // survives a member being appended in a different order than someone expected.
        builder.Property(m => m.SubjectKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(m => m.SuggestionSource).HasConversion<string>().HasMaxLength(32).IsRequired();

        // Repointed is a fact about the last Confirm call, read by the handler in the same unit of
        // work; there is nothing for it to mean once the transaction closes.
        builder.Ignore(m => m.Repointed);

        // The identity of a mapping. Discovery relies on this to be idempotent: it looks a subject
        // up by this pair and only inserts when nothing holds it.
        builder.HasIndex(m => new { m.SubjectKind, m.SubjectKey }).IsUnique();

        // The mapping queue's own ordering — everything a reviewer has yet to decide on.
        builder.HasIndex(m => m.Status);
    }
}
