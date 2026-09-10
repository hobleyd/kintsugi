using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class InstalledPackageConfiguration : IEntityTypeConfiguration<InstalledPackage>
{
    public void Configure(EntityTypeBuilder<InstalledPackage> builder)
    {
        builder.ToTable("installed_packages");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).IsRequired().HasMaxLength(255);
        // Longer than InstalledApplication.Version's 64: a distribution version carries its
        // packaging revision and sometimes an epoch, e.g. "2:8.2.3995-1ubuntu2.24".
        builder.Property(p => p.Version).IsRequired().HasMaxLength(128);
        builder.Property(p => p.Source).IsRequired().HasMaxLength(16);

        builder.HasOne<Host>()
            .WithMany()
            .HasForeignKey(p => p.HostId)
            // Matches installed_applications: a removed host's inventory goes with it.
            .OnDelete(DeleteBehavior.Cascade);

        // The whole table is deleted and rewritten per host on every inventory report, so this is
        // the index that matters. A thousand-odd rows per Linux host makes it worth having.
        builder.HasIndex(p => p.HostId);

        // How the assessment finds which hosts run a given (package, version), and what the
        // discovery pass groups by.
        builder.HasIndex(p => new { p.Name, p.Version });
    }
}
