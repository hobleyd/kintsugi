using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class ApprovedScriptConfiguration : IEntityTypeConfiguration<ApprovedScript>
{
    public void Configure(EntityTypeBuilder<ApprovedScript> builder)
    {
        builder.ToTable("approved_scripts");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Sha256).HasMaxLength(64).IsRequired();
        // 32, matching upgrade_paths.Platform (see UpgradePathConfiguration) — these hold values
        // from the same PlatformBucket namespace, and a shorter column here would silently truncate
        // a `pm:` bucket into one that resolves to nothing.
        builder.Property(s => s.PlatformBucket).HasMaxLength(32).IsRequired();
        builder.Property(s => s.Script).HasColumnType("text").IsRequired();
        builder.Property(s => s.ApplicationName).HasMaxLength(255).IsRequired();
        builder.Property(s => s.ApplicationIdentifier).HasMaxLength(255);
        // "SHA256:" + base64 of a 32-byte digest — 51 characters; 128 leaves room for a different
        // digest without a migration.
        builder.Property(s => s.SignerFingerprint).HasMaxLength(128).IsRequired();
        // text, not a length: a PEM SubjectPublicKeyInfo block runs past 256 characters even for
        // P-256, so the 256 that upgrade_paths.ScriptSignature uses would reject every real key.
        builder.Property(s => s.SignerPublicKeyPem).HasColumnType("text").IsRequired();
        builder.Property(s => s.Signature).HasMaxLength(256).IsRequired();
        builder.Property(s => s.SignedBy).HasMaxLength(255);
        builder.Property(s => s.ApprovedAtUtc).IsRequired();
        builder.Property(s => s.SourceCommitSha).HasMaxLength(64).IsRequired();
        builder.Property(s => s.ImportedAtUtc).IsRequired();

        // Content plus signer, not content alone: two servers may each approve the same bytes, and
        // both attributions are worth keeping — the page shows who vouched for what.
        builder.HasIndex(s => new { s.Sha256, s.SignerFingerprint }).IsUnique();
        // Adoption looks up candidates for an (application, bucket) a local row hasn't resolved.
        builder.HasIndex(s => new { s.ApplicationName, s.PlatformBucket });
    }
}
