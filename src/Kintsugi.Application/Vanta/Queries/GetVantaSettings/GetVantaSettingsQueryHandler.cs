using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Vanta.Queries.GetVantaSettings;

public class GetVantaSettingsQueryHandler : IRequestHandler<GetVantaSettingsQuery, VantaSettingsDto>
{
    private readonly IVantaSettingsRepository _repository;
    private readonly IVantaSettingsProvider _provider;

    public GetVantaSettingsQueryHandler(IVantaSettingsRepository repository, IVantaSettingsProvider provider)
    {
        _repository = repository;
        _provider = provider;
    }

    public async Task<VantaSettingsDto> Handle(GetVantaSettingsQuery request, CancellationToken cancellationToken)
    {
        // Both, for the reason GetGitHubSettingsQueryHandler reads both: the provider gives the
        // effective values, the stored row says whether a value was chosen or merely defaulted.
        var stored = await _repository.GetAsync(cancellationToken);
        var effective = await _provider.GetAsync(cancellationToken);

        return new VantaSettingsDto(
            effective.Enabled,
            effective.ClientId,
            !string.IsNullOrWhiteSpace(stored?.ClientSecret),
            effective.ApiBaseUrl,
            string.IsNullOrWhiteSpace(stored?.ApiBaseUrl),
            effective.VulnerableComponentResourceId,
            effective.PackageVulnerabilityResourceId,
            effective.ConsoleBaseUrl,
            effective.Severity,
            effective.SyncIntervalHours,
            stored?.IsConfigured ?? false);
    }
}
