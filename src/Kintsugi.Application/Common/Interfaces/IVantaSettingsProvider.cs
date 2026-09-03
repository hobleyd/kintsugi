namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// The resolved Vanta configuration — stored settings with defaults filled in — as every consumer
/// should see it.
/// </summary>
/// <param name="ApiBaseUrl">Never null: the stored value or <c>https://api.vanta.com</c>.</param>
/// <param name="ConsoleBaseUrl">This server's browser-facing address, used to build each resource's
/// <c>externalUrl</c>. Null when unconfigured, which is one of the things that makes
/// <see cref="VantaSettingsSnapshot.CanSync"/> false.</param>
public record VantaSettingsSnapshot(
    bool Enabled,
    string? ClientId,
    string? ClientSecret,
    string ApiBaseUrl,
    string? VulnerableComponentResourceId,
    string? PackageVulnerabilityResourceId,
    string? ConsoleBaseUrl,
    double Severity,
    int SyncIntervalHours)
{
    /// <summary>Whether a sync can actually be attempted right now: switched on and completely
    /// configured. The settings page reports both halves separately, so "enabled but not yet
    /// usable" is visible rather than being discovered as a timer that does nothing.</summary>
    public bool CanSync =>
        Enabled
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(VulnerableComponentResourceId)
        && !string.IsNullOrWhiteSpace(PackageVulnerabilityResourceId)
        && !string.IsNullOrWhiteSpace(ConsoleBaseUrl);
}

/// <summary>
/// Reads the Vanta configuration for whoever needs it right now.
/// </summary>
/// <remarks>
/// A provider rather than a constructor-injected <c>IConfiguration</c> read, for the reason spelled
/// out on <see cref="IGitHubSettingsProvider"/>: these values are edited on a settings page while
/// the process runs, so anything that captures them at construction silently ignores every later
/// edit. The Vanta client reads through here per call and attaches its bearer token to the
/// individual request rather than to <c>HttpClient.DefaultRequestHeaders</c>.
/// </remarks>
public interface IVantaSettingsProvider
{
    Task<VantaSettingsSnapshot> GetAsync(CancellationToken cancellationToken);
}
