using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Singleton configuration for pushing this fleet's patch state into Vanta as compliance evidence,
/// via Vanta's "Build integrations" resource-sync API (https://developer.vanta.com/reference/build-integrations.json).
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="GitHubSettings"/> — one row, a blank secret means "keep the stored one",
/// and nothing here is ever returned to a browser (see <c>VantaSettingsDto</c>). It differs in one
/// way deliberately: there is no environment seeding. <c>SeedGitHubSettingsFromEnvironmentAsync</c>
/// exists to carry deployments that predate that page across; this integration has never had
/// environment variables, so adding some would create the second source of truth that seeding was
/// written to remove.
/// </para>
/// <para>
/// Nothing may capture these values at construction — see <c>IVantaSettingsProvider</c>. That is the
/// same rule the GitHub clients follow and for the same reason: an administrator can rotate the
/// client secret while the process is running, and a token pinned to an <c>HttpClient</c>'s default
/// headers would go on being sent until a restart.
/// </para>
/// </remarks>
public class VantaSettings : BaseEntity
{
    /// <summary>Default when <see cref="ApiBaseUrl"/> is unset — Vanta's commercial host. FedRAMP
    /// tenants set <c>https://api.vanta-gov.com</c> instead; the spec lists exactly those two, and
    /// they are stored as a URL rather than an enum so no ordinal-versus-name decision reaches the
    /// wire (see CLAUDE.md on enum serialization).</summary>
    public const string DefaultApiBaseUrl = "https://api.vanta.com";

    /// <summary>The severity reported for an out-of-date package when nothing better is known,
    /// which is always: see <see cref="Severity"/>.</summary>
    public const double DefaultSeverity = 5.0d;

    public const int DefaultSyncIntervalHours = 24;

    /// <summary>Whether the scheduled sync runs at all. False leaves every route and the background
    /// service in place but inert, so turning the integration off never means deleting credentials.</summary>
    public bool Enabled { get; private set; }

    /// <summary>OAuth client ID of the private "Build integrations" app created in Vanta's developer
    /// console. Paired with <see cref="ClientSecret"/> in a <c>client_credentials</c> exchange.</summary>
    public string? ClientId { get; private set; }

    /// <summary>OAuth client secret. Stored as written, like <see cref="AiAgentSettings.ApiKey"/> and
    /// <see cref="GitHubSettings.ScriptApprovalToken"/> — the database is not a secret store; what
    /// protects it is that no route ever returns it.</summary>
    public string? ClientSecret { get; private set; }

    /// <summary>Null means <see cref="DefaultApiBaseUrl"/>, resolved at read time so the default
    /// lives in one place rather than being written into the row.</summary>
    public string? ApiBaseUrl { get; private set; }

    /// <summary>The Vanta-generated resource ID for this app's registered <c>VulnerableComponent</c>
    /// resource, copied from the developer console's Resources tab. Vanta rejects a sync naming an
    /// ID it did not issue, so there is no sensible default.</summary>
    public string? VulnerableComponentResourceId { get; private set; }

    /// <summary>Same, for the app's registered <c>PackageVulnerabilityConnectors</c> resource. The
    /// two are separate registrations in Vanta and must not be conflated.</summary>
    public string? PackageVulnerabilityResourceId { get; private set; }

    /// <summary>
    /// This server's own browser-facing address, used to build each resource's <c>externalUrl</c> —
    /// the "view this in the partner's product" link Vanta shows in its inventory.
    /// </summary>
    /// <remarks>
    /// Deliberately its own setting rather than <c>AGENT_API_BASE_URL</c>: that names nginx's agent
    /// door, which in a split deployment is a different hostname from the one an administrator opens
    /// the admin UI on (see CLAUDE.md, "The fallback is a guess"). It also cannot be derived from the
    /// request, because the sync usually runs on a timer with no request in flight. Vanta requires
    /// HTTPS here and rejects the whole payload otherwise, which is why <see cref="Apply"/> refuses
    /// anything else rather than letting it fail as an opaque 400 a day later.
    /// </remarks>
    public string? ConsoleBaseUrl { get; private set; }

    /// <summary>
    /// The severity, 0-10, reported on every package-vulnerability record this server syncs.
    /// </summary>
    /// <remarks>
    /// A fixed configured number and not a measurement. Kintsugi knows that an installed version is
    /// behind the latest one; it has no CVE feed and no CVSS vector, so it cannot say how dangerous
    /// being behind is. Vanta makes the field mandatory, so this is the honest shape: one value an
    /// administrator sets to match how their Vanta vulnerability SLAs are banded, applied uniformly.
    /// For the same reason nothing here ever populates <c>cveId</c>, <c>cvss3Score</c> or
    /// <c>cvss3Vector</c> — see <c>VantaResourceBuilder</c>, which leaves them off the wire entirely
    /// rather than sending a plausible-looking guess into a compliance record.
    /// </remarks>
    public double Severity { get; private set; } = DefaultSeverity;

    /// <summary>How often the background sync runs. Each run is a complete state-of-the-world PUT,
    /// so this is a freshness dial and nothing accumulates between runs.</summary>
    public int SyncIntervalHours { get; private set; } = DefaultSyncIntervalHours;

    private VantaSettings()
    {
    }

    public static VantaSettings Create(
        bool enabled,
        string? clientId,
        string? clientSecret,
        string? apiBaseUrl,
        string? vulnerableComponentResourceId,
        string? packageVulnerabilityResourceId,
        string? consoleBaseUrl,
        double? severity,
        int? syncIntervalHours)
    {
        var settings = new VantaSettings();
        settings.Apply(
            enabled, clientId, clientSecret, apiBaseUrl, vulnerableComponentResourceId,
            packageVulnerabilityResourceId, consoleBaseUrl, severity, syncIntervalHours);
        return settings;
    }

    public void Update(
        bool enabled,
        string? clientId,
        string? clientSecret,
        string? apiBaseUrl,
        string? vulnerableComponentResourceId,
        string? packageVulnerabilityResourceId,
        string? consoleBaseUrl,
        double? severity,
        int? syncIntervalHours)
    {
        Apply(
            enabled, clientId, clientSecret, apiBaseUrl, vulnerableComponentResourceId,
            packageVulnerabilityResourceId, consoleBaseUrl, severity, syncIntervalHours);
        MarkUpdated();
    }

    /// <summary>Clears the stored client secret. An explicit act, because a blank one on the way in
    /// means "keep" — the same asymmetry <see cref="GitHubSettings.ClearApiToken"/> exists for.</summary>
    public void ClearClientSecret()
    {
        ClientSecret = null;
        MarkUpdated();
    }

    /// <summary>Whether a sync could actually be attempted. Reported on the settings page rather than
    /// left to be discovered as a scheduled run that quietly does nothing.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(VulnerableComponentResourceId)
        && !string.IsNullOrWhiteSpace(PackageVulnerabilityResourceId)
        && !string.IsNullOrWhiteSpace(ConsoleBaseUrl);

    private void Apply(
        bool enabled,
        string? clientId,
        string? clientSecret,
        string? apiBaseUrl,
        string? vulnerableComponentResourceId,
        string? packageVulnerabilityResourceId,
        string? consoleBaseUrl,
        double? severity,
        int? syncIntervalHours)
    {
        // Blank means "keep", because the page never round-trips the real secret back to the browser
        // and so cannot send it back unchanged.
        ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? ClientSecret : clientSecret.Trim();

        ClientId = Normalize(clientId);
        ApiBaseUrl = NormalizeUrl(apiBaseUrl, nameof(ApiBaseUrl));
        VulnerableComponentResourceId = Normalize(vulnerableComponentResourceId);
        PackageVulnerabilityResourceId = Normalize(packageVulnerabilityResourceId);
        ConsoleBaseUrl = NormalizeUrl(consoleBaseUrl, nameof(ConsoleBaseUrl));

        if (severity is not null)
        {
            if (severity < 0d || severity > 10d)
            {
                throw new DomainException("Severity must be between 0 and 10.");
            }

            // Vanta rounds to the nearest tenth on its side; do it here so what the page shows back
            // is what Vanta actually stored.
            Severity = Math.Round(severity.Value, 1);
        }

        if (syncIntervalHours is not null)
        {
            if (syncIntervalHours < 1 || syncIntervalHours > 168)
            {
                throw new DomainException("The sync interval must be between 1 and 168 hours.");
            }

            SyncIntervalHours = syncIntervalHours.Value;
        }

        // Enabling with a half-filled form would leave the background service failing on a timer with
        // nobody watching, so the invariant lives here rather than only in the validator.
        Enabled = enabled;
        if (Enabled && !IsConfigured)
        {
            throw new DomainException(
                "The Vanta integration needs a client ID, client secret, both resource IDs and this server's address before it can be enabled.");
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Trailing slashes are trimmed so every URL built from this has exactly one separator, and
    /// anything but HTTPS is refused: Vanta requires <c>externalUrl</c> to be an HTTPS URL, and its
    /// own API is HTTPS-only, so an <c>http://</c> value here fails much later and much less
    /// legibly than it does at the point somebody typed it.
    /// </summary>
    private static string? NormalizeUrl(string? value, string field)
    {
        var normalized = Normalize(value)?.TrimEnd('/');
        if (normalized is null)
        {
            return null;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new DomainException($"{field} must be an absolute https:// URL.");
        }

        return normalized;
    }
}
