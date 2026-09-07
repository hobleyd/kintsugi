using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Singleton configuration naming the logging platform audit events are shipped to and how to
/// reach it: Datadog, Grafana Loki, Google Cloud Logging, AWS CloudWatch Logs, Azure Monitor,
/// Splunk HEC, or any endpoint that will accept JSON over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="AuthenticationSettings"/>: one row, one provider, a set of nullable
/// columns of which each provider uses a subset, and <see cref="Apply"/> nulling whatever the
/// chosen provider does not read so a stale value from a previous provider can never be picked up
/// by the next one. Which column means what to which provider is stated on each property, and the
/// Auditing settings screen restates it as instructions — keep the two in step.
/// </para>
/// <para>
/// The secret is stored as written, like <see cref="AuthenticationSettings.ClientSecret"/> and
/// <see cref="VantaSettings.ClientSecret"/>; the database is not a secret store, and what protects
/// it is that no route ever returns it (see <c>AuditSettingsDto</c>). A blank secret on the way in
/// means "keep the stored one", because the page never received the real value and so cannot send
/// it back unchanged. It differs from the authentication settings in one respect: <b>changing the
/// provider drops the stored secret</b>. A Datadog API key is not an AWS secret access key, and
/// carrying one across would mean sending a credential issued by one vendor to another on the first
/// audit event.
/// </para>
/// <para>
/// Nothing may capture these values at construction — the same rule <c>IGitHubSettingsProvider</c>
/// and <c>IVantaSettingsProvider</c> exist for. Whatever ships events reads the row per call, so a
/// rotated key takes effect without a restart.
/// </para>
/// </remarks>
public class AuditSettings : BaseEntity
{
    /// <summary>Default when <see cref="Region"/> is blank for Datadog — the US1 site. Other
    /// sites are <c>datadoghq.eu</c>, <c>us3.datadoghq.com</c>, <c>us5.datadoghq.com</c>,
    /// <c>ap1.datadoghq.com</c> and <c>ddog-gov.com</c>.</summary>
    public const string DefaultDatadogSite = "datadoghq.com";

    public AuditProvider Provider { get; private set; }

    /// <summary>Whether audit events are shipped at all. False keeps the configuration and sends
    /// nothing, so switching auditing off never means deleting credentials.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// The URL events are posted to, for the providers that have one: the Loki push URL (Grafana
    /// Cloud's <c>https://logs-prod-XXX.grafana.net</c>, or a self-hosted instance), the Azure
    /// Monitor data collection endpoint's logs-ingestion URL, the Splunk HEC base URL, or the
    /// generic HTTP endpoint. Null for Datadog (derived from <see cref="Region"/>), Google and AWS
    /// (fixed per project and region).
    /// </summary>
    public string? Endpoint { get; private set; }

    /// <summary>The Datadog site (<c>datadoghq.com</c>, <c>datadoghq.eu</c>, …) or the AWS region
    /// (<c>ap-southeast-2</c>). Both answer "which of the vendor's deployments", which is why they
    /// share a column. Null for every other provider.</summary>
    public string? Region { get; private set; }

    /// <summary>The non-secret half of a credential pair: the AWS access key ID, the Azure
    /// application (client) ID, or the Grafana Cloud username (the numeric instance ID) for basic
    /// authentication against Loki — optional there, since a self-hosted Loki may need none.</summary>
    public string? ClientId { get; private set; }

    /// <summary>
    /// The secret half: the Datadog API key, the Grafana Cloud access-policy token, the Google
    /// service-account key JSON, the AWS secret access key, the Azure client secret, the Splunk
    /// HEC token, or the bearer token for a generic endpoint. Required for every provider except
    /// Loki and the generic endpoint, where an unauthenticated instance is a legitimate target.
    /// </summary>
    /// <remarks>Unbounded, unlike every other secret column in this schema, because a Google
    /// service-account key is a JSON document of a couple of kilobytes rather than a token.</remarks>
    public string? Secret { get; private set; }

    /// <summary>The Azure directory (tenant) ID the client-credentials token is requested from.</summary>
    public string? TenantId { get; private set; }

    /// <summary>The Google Cloud project whose Logging API receives the entries.</summary>
    public string? ProjectId { get; private set; }

    /// <summary>The CloudWatch log group events are written to.</summary>
    public string? LogGroup { get; private set; }

    /// <summary>The Azure data collection rule's immutable ID (<c>dcr-…</c>), which the Logs
    /// Ingestion API names in its path.</summary>
    public string? DataCollectionRuleId { get; private set; }

    /// <summary>The CloudWatch log stream within <see cref="LogGroup"/> (optional; one is derived
    /// when blank), or the Azure stream declared by the data collection rule
    /// (<c>Custom-…_CL</c>, required — the rule routes on it).</summary>
    public string? Stream { get; private set; }

    /// <summary>The Splunk index events are sent to. Optional: blank leaves it to the HEC token's
    /// default index.</summary>
    public string? Index { get; private set; }

    private AuditSettings()
    {
    }

    public static AuditSettings Create(
        AuditProvider provider,
        bool isEnabled,
        string? endpoint,
        string? region,
        string? clientId,
        string? secret,
        string? tenantId,
        string? projectId,
        string? logGroup,
        string? dataCollectionRuleId,
        string? stream,
        string? index)
    {
        var settings = new AuditSettings();
        settings.Apply(
            provider, isEnabled, endpoint, region, clientId, secret, tenantId, projectId, logGroup,
            dataCollectionRuleId, stream, index);
        return settings;
    }

    public void Update(
        AuditProvider provider,
        bool isEnabled,
        string? endpoint,
        string? region,
        string? clientId,
        string? secret,
        string? tenantId,
        string? projectId,
        string? logGroup,
        string? dataCollectionRuleId,
        string? stream,
        string? index)
    {
        Apply(
            provider, isEnabled, endpoint, region, clientId, secret, tenantId, projectId, logGroup,
            dataCollectionRuleId, stream, index);
        MarkUpdated();
    }

    /// <summary>Clears the stored secret. An explicit act, because a blank one on the way in means
    /// "keep" — the same asymmetry <see cref="VantaSettings.ClearClientSecret"/> exists for. Only
    /// meaningful for the providers whose secret is optional; for the others the next
    /// <see cref="Update"/> will refuse to save without a new one.</summary>
    public void ClearSecret()
    {
        Secret = null;
        MarkUpdated();
    }

    /// <summary>Whether the secret is required for <paramref name="provider"/>. Loki and a generic
    /// endpoint may be unauthenticated; everything else is a hosted service with a key.</summary>
    public static bool RequiresSecret(AuditProvider provider) =>
        provider is not (AuditProvider.GrafanaLoki or AuditProvider.GenericHttp);

    private void Apply(
        AuditProvider provider,
        bool isEnabled,
        string? endpoint,
        string? region,
        string? clientId,
        string? secret,
        string? tenantId,
        string? projectId,
        string? logGroup,
        string? dataCollectionRuleId,
        string? stream,
        string? index)
    {
        // A secret belongs to the provider it was issued by. Keeping it across a provider change
        // would ship it to a different vendor, so the change drops it and a required one has to be
        // entered afresh — the screen says so beside the field.
        var storedSecret = provider == Provider ? Secret : null;
        var resolvedSecret = string.IsNullOrWhiteSpace(secret) ? storedSecret : secret.Trim();
        if (RequiresSecret(provider) && string.IsNullOrWhiteSpace(resolvedSecret))
        {
            throw new DomainException($"{SecretName(provider)} is required for {DisplayName(provider)}.");
        }

        Provider = provider;
        IsEnabled = isEnabled;
        Secret = resolvedSecret;

        Endpoint = null;
        Region = null;
        ClientId = null;
        TenantId = null;
        ProjectId = null;
        LogGroup = null;
        DataCollectionRuleId = null;
        Stream = null;
        Index = null;

        switch (provider)
        {
            case AuditProvider.Datadog:
                Region = Normalize(region) ?? DefaultDatadogSite;
                if (Region.Contains("://", StringComparison.Ordinal) || Region.Contains('/'))
                {
                    throw new DomainException("The Datadog site is a hostname such as datadoghq.com or datadoghq.eu, not a URL.");
                }
                break;

            case AuditProvider.GrafanaLoki:
                Endpoint = RequireUrl(endpoint, "The Loki push URL");
                ClientId = Normalize(clientId);
                break;

            case AuditProvider.GoogleCloudLogging:
                ProjectId = Require(projectId, "A Google Cloud project ID");
                break;

            case AuditProvider.AwsCloudWatch:
                Region = Require(region, "An AWS region");
                ClientId = Require(clientId, "An access key ID");
                LogGroup = Require(logGroup, "A log group name");
                Stream = Normalize(stream);
                break;

            case AuditProvider.AzureMonitor:
                Endpoint = RequireUrl(endpoint, "The data collection endpoint's logs-ingestion URL");
                TenantId = Require(tenantId, "A directory (tenant) ID");
                ClientId = Require(clientId, "An application (client) ID");
                DataCollectionRuleId = Require(dataCollectionRuleId, "The data collection rule's immutable ID");
                Stream = Require(stream, "The stream name declared by the data collection rule");
                break;

            case AuditProvider.SplunkHec:
                Endpoint = RequireUrl(endpoint, "The HTTP Event Collector URL");
                Index = Normalize(index);
                break;

            case AuditProvider.GenericHttp:
                Endpoint = RequireUrl(endpoint, "The endpoint URL");
                break;

            default:
                throw new DomainException($"Unknown audit provider {provider}.");
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Require(string? value, string what) =>
        Normalize(value) ?? throw new DomainException($"{what} is required.");

    /// <summary>
    /// Trailing slashes are trimmed so every path built on this has exactly one separator. Plain
    /// HTTP is allowed, unlike <see cref="VantaSettings"/>'s URLs: a self-hosted Loki or Splunk on
    /// an internal network is exactly the kind of target this provider list exists for.
    /// </summary>
    private static string RequireUrl(string? value, string what)
    {
        var normalized = Normalize(value)?.TrimEnd('/');
        if (normalized is null)
        {
            throw new DomainException($"{what} is required.");
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new DomainException($"{what} must be an absolute http:// or https:// URL.");
        }

        return normalized;
    }

    private static string SecretName(AuditProvider provider) => provider switch
    {
        AuditProvider.Datadog => "An API key",
        AuditProvider.GoogleCloudLogging => "A service account key",
        AuditProvider.AwsCloudWatch => "A secret access key",
        AuditProvider.AzureMonitor => "A client secret",
        AuditProvider.SplunkHec => "An HEC token",
        _ => "A token"
    };

    /// <summary>The name the screen shows for each provider, for error messages that name it.</summary>
    public static string DisplayName(AuditProvider provider) => provider switch
    {
        AuditProvider.Datadog => "Datadog",
        AuditProvider.GrafanaLoki => "Grafana Loki",
        AuditProvider.GoogleCloudLogging => "Google Cloud Logging",
        AuditProvider.AwsCloudWatch => "AWS CloudWatch Logs",
        AuditProvider.AzureMonitor => "Azure Monitor",
        AuditProvider.SplunkHec => "Splunk HEC",
        AuditProvider.GenericHttp => "a generic HTTP endpoint",
        _ => provider.ToString()
    };
}
