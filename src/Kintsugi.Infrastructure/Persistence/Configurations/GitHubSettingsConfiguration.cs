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

        // The GitHub App. A slug or login is at most 39 characters on GitHub; 100 leaves room. The
        // private key is an RSA PEM — about 1.7 KB for the 2048-bit keys GitHub issues today — and
        // is left unbounded rather than sized to today's key length.
        builder.Property(s => s.GitHubAppSlug).HasMaxLength(100);
        builder.Property(s => s.GitHubAppOwner).HasMaxLength(100);
        builder.Property(s => s.GitHubAppPrivateKey);
        builder.Ignore(s => s.HasGitHubApp);
        builder.Ignore(s => s.IsGitHubAppInstalled);
    }
}
