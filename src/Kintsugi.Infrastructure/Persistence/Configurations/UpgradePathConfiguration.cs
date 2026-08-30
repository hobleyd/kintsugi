using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class UpgradePathConfiguration : IEntityTypeConfiguration<UpgradePath>
{
    public void Configure(EntityTypeBuilder<UpgradePath> builder)
    {
        builder.ToTable("upgrade_paths");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.ApplicationName).HasMaxLength(255).IsRequired();
        builder.Property(p => p.Platform).HasMaxLength(32).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(p => p.LatestVersion).HasMaxLength(64);
        builder.Property(p => p.Method).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(p => p.DownloadUrl).HasMaxLength(2048);
        builder.Property(p => p.Command).HasMaxLength(2048);
        builder.Property(p => p.Instructions).HasMaxLength(4000);
        builder.Property(p => p.SourceUrl).HasMaxLength(2048);
        builder.Property(p => p.Notes).HasMaxLength(2000);
        builder.Property(p => p.Script).HasColumnType("text");
        builder.Property(p => p.ApplicationIdentifier).HasMaxLength(255);
        builder.Property(p => p.CheckedUtc).IsRequired();
        builder.Property(p => p.ScriptSignature).HasMaxLength(256);
        builder.Property(p => p.CommandSignature).HasMaxLength(256);

        builder.HasIndex(p => new { p.ApplicationName, p.Platform }).IsUnique();
        // Not unique: a Windows/Linux row could theoretically share a name with an unrelated
        // macOS bundle identifier collision is vanishingly unlikely, but nothing enforces it isn't
        // possible — the scripts endpoint takes the first match rather than assuming uniqueness.
        builder.HasIndex(p => p.ApplicationIdentifier);
    }
}
