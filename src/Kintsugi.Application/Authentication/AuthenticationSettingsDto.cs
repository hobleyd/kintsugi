using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Authentication;

/// <summary>The raw client secret is never returned to the client; <see cref="HasClientSecret"/> reports whether one is stored.</summary>
public record AuthenticationSettingsDto(
    AuthProvider Provider,
    string? ClientId,
    string? Authority,
    string? TenantId,
    string? HostedDomain,
    bool IsEnabled,
    bool HasClientSecret,
    IReadOnlyList<string> AdminEmails)
{
    public static AuthenticationSettingsDto FromEntity(AuthenticationSettings entity) =>
        new(entity.Provider, entity.ClientId, entity.Authority, entity.TenantId, entity.HostedDomain, entity.IsEnabled, !string.IsNullOrEmpty(entity.ClientSecret), entity.AdminEmails.ToList());

    public static AuthenticationSettingsDto NotConfigured() =>
        new(AuthProvider.GoogleWorkspace, null, null, null, null, IsEnabled: false, HasClientSecret: false, AdminEmails: Array.Empty<string>());
}
