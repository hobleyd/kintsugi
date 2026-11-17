using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class BaselineVersionConfiguration : IEntityTypeConfiguration<BaselineVersion>
{
    public void Configure(EntityTypeBuilder<BaselineVersion> builder)
    {
        builder.ToTable("baseline_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Document).IsRequired();
        builder.Property(v => v.DocumentSha256).HasMaxLength(64).IsRequired();
        builder.Property(v => v.Signature).HasMaxLength(256);
        builder.Property(v => v.CreatedBy).HasMaxLength(255);
        builder.Property(v => v.SignedBy).HasMaxLength(255);
        builder.Ignore(v => v.IsSigned);
        builder.HasIndex(v => new { v.BaselineId, v.Number }).IsUnique();
        builder.HasOne<Baseline>().WithMany().HasForeignKey(v => v.BaselineId).OnDelete(DeleteBehavior.Cascade);
    }
}
