using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;
using Kintsugi.Infrastructure.Ai;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class AiConnectionConfiguration : IEntityTypeConfiguration<AiConnection>
{
    public void Configure(EntityTypeBuilder<AiConnection> builder)
    {
        builder.ToTable("ai_connections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.CatalogProviderId).HasMaxLength(100);
        // Names, not ordinals, like AiAgentSettings.Provider: the database stays indifferent to
        // declaration order.
        builder.Property(c => c.Protocol).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(c => c.AuthMode).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(c => c.BaseUrl).HasMaxLength(512);
        builder.Property(c => c.ApiKey).HasMaxLength(1024);
        builder.Property(c => c.GoogleCloudProject).HasMaxLength(64);
        builder.Property(c => c.GoogleCloudLocation).HasMaxLength(64);
    }
}

public class AiFeatureRouteConfiguration : IEntityTypeConfiguration<AiFeatureRoute>
{
    public void Configure(EntityTypeBuilder<AiFeatureRoute> builder)
    {
        builder.ToTable("ai_feature_routes");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Feature).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(r => r.Feature).IsUnique();
        builder.Property(r => r.Model).HasMaxLength(200).IsRequired();
        // Restrict, not cascade: DeleteAiConnectionCommand refuses to delete a connection a route
        // uses, and the database says the same thing if anything ever tries to.
        builder.HasOne<AiConnection>().WithMany().HasForeignKey(r => r.ConnectionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AiCatalogCacheEntryConfiguration : IEntityTypeConfiguration<AiCatalogCacheEntry>
{
    public void Configure(EntityTypeBuilder<AiCatalogCacheEntry> builder)
    {
        builder.ToTable("ai_catalog_cache");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Json).IsRequired();
    }
}
