using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Singleton configuration describing the OAuth2/OIDC identity provider (Google Workspace,
/// Microsoft Entra, a generic OIDC provider such as Auth0 or Okta, or Clerk) users must sign in
/// through to reach the site, and how to reach it.
/// </summary>
public class AuthenticationSettings : BaseEntity
{
    public AuthProvider Provider { get; private set; }
    public string? ClientId { get; private set; }
    public string? ClientSecret { get; private set; }
    public string? Authority { get; private set; }
    public string? TenantId { get; private set; }
    public string? HostedDomain { get; private set; }
    public bool IsEnabled { get; private set; }

    private AuthenticationSettings()
    {
    }

    public static AuthenticationSettings Create(
        AuthProvider provider, string? clientId, string? clientSecret, string? authority, string? tenantId, string? hostedDomain, bool isEnabled)
    {
        var settings = new AuthenticationSettings();
        settings.Apply(provider, clientId, clientSecret, authority, tenantId, hostedDomain, isEnabled);
        return settings;
    }

    public void Update(
        AuthProvider provider, string? clientId, string? clientSecret, string? authority, string? tenantId, string? hostedDomain, bool isEnabled)
    {
        Apply(provider, clientId, clientSecret, authority, tenantId, hostedDomain, isEnabled);
        MarkUpdated();
    }

    /// <summary>The issuer URL the OIDC handler should use to discover the provider's endpoints
    /// (<c>/.well-known/openid-configuration</c>). Google and Microsoft Entra have a fixed or
    /// computed authority; a generic OIDC provider or Clerk supply their own.</summary>
    public string? ResolveAuthority() => Provider switch
    {
        AuthProvider.GoogleWorkspace => "https://accounts.google.com",
        AuthProvider.MicrosoftEntra => string.IsNullOrWhiteSpace(TenantId) ? null : $"https://login.microsoftonline.com/{TenantId}/v2.0",
        _ => Authority
    };

    // Blank clientSecret on an update means "keep the currently stored secret" rather than clear
    // it, since the UI never round-trips the real secret back to the browser.
    private void Apply(
        AuthProvider provider, string? clientId, string? clientSecret, string? authority, string? tenantId, string? hostedDomain, bool isEnabled)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new DomainException($"A client ID is required for {provider}.");
        }

        var resolvedClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? ClientSecret : clientSecret;
        if (string.IsNullOrWhiteSpace(resolvedClientSecret))
        {
            throw new DomainException($"A client secret is required for {provider}.");
        }

        Provider = provider;
        ClientId = clientId;
        ClientSecret = resolvedClientSecret;
        IsEnabled = isEnabled;

        switch (provider)
        {
            case AuthProvider.GoogleWorkspace:
                Authority = null;
                TenantId = null;
                HostedDomain = string.IsNullOrWhiteSpace(hostedDomain) ? null : hostedDomain;
                break;

            case AuthProvider.MicrosoftEntra:
                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    throw new DomainException("A tenant ID is required for Microsoft Entra.");
                }

                Authority = null;
                TenantId = tenantId;
                HostedDomain = null;
                break;

            case AuthProvider.GenericOidc:
            case AuthProvider.Clerk:
                if (string.IsNullOrWhiteSpace(authority))
                {
                    throw new DomainException($"An authority (issuer) URL is required for {provider}.");
                }

                Authority = authority;
                TenantId = null;
                HostedDomain = null;
                break;
        }
    }
}
