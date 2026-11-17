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

    /// <summary>
    /// The emails allowed to use the web UI once sign-in is required, lower-cased and distinct.
    /// <b>Empty means "every signed-in user is an administrator"</b> — today's behaviour — so that
    /// deploying the feature cannot lock anyone out; an administrator fills the list afterwards.
    /// Held in the database rather than in configuration because this repository is public.
    /// </summary>
    public List<string> AdminEmails { get; private set; } = new();

    /// <summary>
    /// Who put each of <see cref="AdminEmails"/> on the list, and when — what the Administrators page
    /// shows. Deliberately a side record rather than a change to the list itself, because the list
    /// is what sign-in checks and a lockout guard reasons about; this only describes it. An email
    /// with no entry here was on the list before this was tracked, and is shown as such. Replaced
    /// wholesale on every change, never edited in place, so EF sees it change.
    /// </summary>
    public List<AdminAddition> AdminAdditions { get; private set; } = new();

    private AuthenticationSettings()
    {
    }

    public static AuthenticationSettings Create(
        AuthProvider provider, string? clientId, string? clientSecret, string? authority, string? tenantId, string? hostedDomain, bool isEnabled,
        IEnumerable<string>? adminEmails = null)
    {
        var settings = new AuthenticationSettings();
        settings.Apply(provider, clientId, clientSecret, authority, tenantId, hostedDomain, isEnabled, adminEmails);
        return settings;
    }

    public void Update(
        AuthProvider provider, string? clientId, string? clientSecret, string? authority, string? tenantId, string? hostedDomain, bool isEnabled,
        IEnumerable<string>? adminEmails = null)
    {
        Apply(provider, clientId, clientSecret, authority, tenantId, hostedDomain, isEnabled, adminEmails);
        MarkUpdated();
    }

    /// <summary>
    /// Puts <paramref name="emails"/> on the list as added by <paramref name="addedBy"/> at
    /// <paramref name="whenUtc"/>. Ones already listed keep their original record, so adding a
    /// colleague twice cannot rewrite who invited them.
    /// </summary>
    public void AddAdmins(IEnumerable<string?> emails, string? addedBy, DateTimeOffset whenUtc)
    {
        var fresh = NormaliseAdminEmails(emails).Where(e => !AdminEmails.Contains(e)).ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        AdminEmails = AdminEmails.Concat(fresh).ToList();
        AdminAdditions = AdminAdditions
            .Concat(fresh.Select(e => new AdminAddition(e, NormaliseAddedBy(addedBy), whenUtc)))
            .ToList();
        MarkUpdated();
    }

    /// <summary>Takes <paramref name="email"/> off the list, and drops its record. Does nothing if it
    /// is not there. Refuses to remove the last entry: an empty list reopens the site to every
    /// signed-in account (see <see cref="AdminEmails"/>), and every one of them could then sign
    /// scripts that agents run as root. That must be a deliberate act — clearing the list on the
    /// Authentication screen, through <see cref="Update"/> — never one click on the Administrators
    /// page. Checked here, on the list as freshly loaded, rather than only in the controller, so two
    /// concurrent removals judged against the same stale list still cannot both pass.</summary>
    /// <exception cref="DomainException">Removing <paramref name="email"/> would leave the list empty.</exception>
    public void RemoveAdmin(string? email)
    {
        var normalised = NormaliseAdminEmails(new[] { email }).SingleOrDefault();
        if (normalised is null || !AdminEmails.Contains(normalised))
        {
            return;
        }

        if (AdminEmails.Count == 1)
        {
            throw new DomainException(
                "The last administrator can't be removed here: an empty list makes every account that can sign in an "
                + "administrator. Add another administrator first, or clear the list deliberately in Settings > Authentication.");
        }

        AdminEmails = AdminEmails.Where(e => e != normalised).ToList();
        AdminAdditions = AdminAdditions.Where(a => !SameEmail(a.Email, normalised)).ToList();
        MarkUpdated();
    }

    /// <summary>
    /// Every listed email with its record, in list order. An email with no record — one that was
    /// listed before additions were tracked — comes back with no adder and no date.
    /// </summary>
    public IReadOnlyList<(string Email, string? AddedBy, DateTimeOffset? AddedUtc)> AdminRecords() =>
        AdminEmails
            .Select(email =>
            {
                var record = AdminAdditions.FirstOrDefault(a => SameEmail(a.Email, email));
                return (email, record?.AddedBy, (DateTimeOffset?)record?.AddedUtc);
            })
            .ToList();

    private static bool SameEmail(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? NormaliseAddedBy(string? addedBy) =>
        string.IsNullOrWhiteSpace(addedBy) ? null : addedBy.Trim().ToLowerInvariant();

    /// <summary>
    /// Whether <paramref name="email"/> may administer the site. An empty allowlist admits everyone
    /// (see <see cref="AdminEmails"/>); otherwise the comparison is case-insensitive and a null or
    /// blank email never matches.
    /// </summary>
    public bool IsAdminEmail(string? email) =>
        AdminEmails.Count == 0
        || (!string.IsNullOrWhiteSpace(email) && AdminEmails.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// A submitted allowlist as it will be stored: blank and null entries dropped, the rest trimmed,
    /// lower-cased and de-duplicated. Public so <c>AdminSettingsController</c>'s lockout guard checks
    /// the list the caller will actually be held to, not the one they sent — a null entry once
    /// matched a caller with no email there, and was then dropped here, locking that caller out.
    /// </summary>
    public static List<string> NormaliseAdminEmails(IEnumerable<string?> adminEmails) =>
        adminEmails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

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
    // it, since the UI never round-trips the real secret back to the browser. A null adminEmails
    // likewise means "keep the current list" — a client that predates the field must not wipe it —
    // whereas an empty list deliberately clears it.
    private void Apply(
        AuthProvider provider, string? clientId, string? clientSecret, string? authority, string? tenantId, string? hostedDomain, bool isEnabled,
        IEnumerable<string>? adminEmails)
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

        if (adminEmails is not null)
        {
            var before = AdminEmails;
            AdminEmails = NormaliseAdminEmails(adminEmails);

            // This path (the Authentication screen's text box) does not know who is saving, so an
            // address it introduces is recorded with no adder; one it drops loses its record.
            var now = DateTimeOffset.UtcNow;
            AdminAdditions = AdminEmails
                .Select(e => AdminAdditions.FirstOrDefault(a => SameEmail(a.Email, e))
                    ?? (before.Contains(e) ? null : new AdminAddition(e, null, now)))
                .Where(a => a is not null)
                .Select(a => a!)
                .ToList();
        }

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

/// <summary>One entry of <see cref="AuthenticationSettings.AdminAdditions"/>: an administrator's
/// email, who added them (null when the Authentication screen's text box did, which does not know
/// who is saving) and when.</summary>
public sealed record AdminAddition(string Email, string? AddedBy, DateTimeOffset AddedUtc);
