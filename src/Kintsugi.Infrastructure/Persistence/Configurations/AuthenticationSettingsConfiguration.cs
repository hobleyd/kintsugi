using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class AuthenticationSettingsConfiguration : IEntityTypeConfiguration<AuthenticationSettings>
{
    public void Configure(EntityTypeBuilder<AuthenticationSettings> builder)
    {
        builder.ToTable("authentication_settings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.ClientId).HasMaxLength(256);
        builder.Property(s => s.ClientSecret).HasMaxLength(512);
        builder.Property(s => s.Authority).HasMaxLength(512);
        builder.Property(s => s.TenantId).HasMaxLength(128);
        builder.Property(s => s.HostedDomain).HasMaxLength(255);
        builder.Property(s => s.IsEnabled).IsRequired();
        // A Postgres text[]; an empty array is the "no allowlist, every signed-in user is an
        // administrator" state, so existing rows migrate to today's behaviour.
        builder.Property(s => s.AdminEmails).HasColumnType("text[]").HasDefaultValueSql("'{}'").IsRequired();
        // Who added each administrator and when, as JSON. Existing rows start with none, which the
        // Administrators page shows as "before tracking".
        // Converted to JSON text rather than mapped as a jsonb POCO list so the in-memory provider the
        // repository tests use can hold it too.
        builder.Property(s => s.AdminAdditions)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<AdminAddition>>(v, (JsonSerializerOptions?)null) ?? new List<AdminAddition>(),
                new ValueComparer<List<AdminAddition>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                    v => v.ToList()))
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb")
            .IsRequired();
    }
}
