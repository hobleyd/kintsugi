using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class HostFactConfiguration : IEntityTypeConfiguration<HostFact>
{
    public void Configure(EntityTypeBuilder<HostFact> builder)
    {
        builder.ToTable("host_facts");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Key).HasMaxLength(100).IsRequired();
        builder.Property(f => f.Value).HasMaxLength(HostFact.MaxValueLength);
        builder.HasIndex(f => new { f.HostId, f.Key }).IsUnique();
        builder.HasOne<Host>().WithMany().HasForeignKey(f => f.HostId).OnDelete(DeleteBehavior.Cascade);
    }
}
