namespace Kintsugi.Application.Vanta;

/// <summary>
/// The Vanta settings as the settings page sees them.
/// </summary>
/// <remarks>
/// The client secret is never returned — <see cref="HasClientSecret"/> reports only whether one is
/// stored, the same contract <c>GitHubSettingsDto</c> and <c>AiAgentSettingsDto</c> have for their
/// tokens, and the reason a blank secret on save means "keep the stored one".
/// </remarks>
/// <param name="ApiBaseUrl">Resolved — the stored value, or Vanta's commercial host when none is
/// stored. Shown as the effective value rather than an empty box.</param>
/// <param name="IsApiBaseUrlDefault">Whether the value above is the default rather than something an
/// administrator chose (a FedRAMP tenant points this at api.vanta-gov.com).</param>
/// <param name="IsConfigured">Whether a sync could run if it were enabled. Reported separately from
/// <paramref name="Enabled"/> so "switched on but missing a resource ID" is visible on the page
/// rather than discovered as a nightly job that quietly does nothing.</param>
public record VantaSettingsDto(
    bool Enabled,
    string? ClientId,
    bool HasClientSecret,
    string ApiBaseUrl,
    bool IsApiBaseUrlDefault,
    string? VulnerableComponentResourceId,
    string? PackageVulnerabilityResourceId,
    string? ConsoleBaseUrl,
    double Severity,
    int SyncIntervalHours,
    bool IsConfigured);
