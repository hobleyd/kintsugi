using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Vanta;

/// <inheritdoc cref="IVantaSettingsProvider" />
public class VantaSettingsProvider : IVantaSettingsProvider
{
    private readonly IVantaSettingsRepository _repository;

    public VantaSettingsProvider(IVantaSettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task<VantaSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        // No caching, for the reason GitHubSettingsProvider gives: an edit has to take effect on the
        // next run, and this query is dwarfed by the HTTP calls it precedes.
        var settings = await _repository.GetAsync(cancellationToken);

        return new VantaSettingsSnapshot(
            settings?.Enabled ?? false,
            NullIfBlank(settings?.ClientId),
            NullIfBlank(settings?.ClientSecret),
            string.IsNullOrWhiteSpace(settings?.ApiBaseUrl) ? VantaSettings.DefaultApiBaseUrl : settings.ApiBaseUrl.TrimEnd('/'),
            NullIfBlank(settings?.VulnerableComponentResourceId),
            NullIfBlank(settings?.PackageVulnerabilityResourceId),
            NullIfBlank(settings?.ConsoleBaseUrl)?.TrimEnd('/'),
            settings?.Severity ?? VantaSettings.DefaultSeverity,
            settings?.SyncIntervalHours ?? VantaSettings.DefaultSyncIntervalHours);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
