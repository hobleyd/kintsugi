using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Configurations;

public class GitHubSettingsConfiguration : IEntityTypeConfiguration<GitHubSettings>
{
    public void Configure(EntityTypeBuilder<GitHubSettings> builder)
    {
        builder.ToTable("github_settings");

        builder.HasKey(s => s.Id);

        // 512 matching AuthenticationSettings.ClientSecret — a GitHub fine-grained token is a little
        // over 90 characters today, and this leaves room for whatever replaces it.
        builder.Property(s => s.ApiToken).HasMaxLength(512);
        builder.Property(s => s.ScriptApprovalToken).HasMaxLength(512);
        // "owner/repo"; GitHub caps each half at 100 characters.
        builder.Property(s => s.AgentPackageRepository).HasMaxLength(255);
        builder.Property(s => s.ScriptApprovalRepository).HasMaxLength(255);
    }
}
