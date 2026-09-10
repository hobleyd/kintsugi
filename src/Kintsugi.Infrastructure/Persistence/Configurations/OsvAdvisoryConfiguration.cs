using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class OsvAdvisoryConfiguration : IEntityTypeConfiguration<OsvAdvisory>
{
    public void Configure(EntityTypeBuilder<OsvAdvisory> builder)
    {
        builder.ToTable("osv_advisories");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.OsvId).IsRequired().HasMaxLength(128);
        // Space-separated CVE ids. An advisory standing for a dozen is unusual but real, so the
        // column is sized for it rather than for the common one or two.
        builder.Property(a => a.CveIds).IsRequired().HasMaxLength(1024);

        builder.Ignore(a => a.Cves);

        builder.HasIndex(a => a.OsvId).IsUnique();
    }
}
