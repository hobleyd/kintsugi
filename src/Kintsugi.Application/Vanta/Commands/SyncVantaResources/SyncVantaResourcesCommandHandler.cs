using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Vanta.Commands.SyncVantaResources;

public class SyncVantaResourcesCommandHandler : IRequestHandler<SyncVantaResourcesCommand, VantaSyncResultDto>
{
    private readonly IVantaSettingsProvider _settingsProvider;
    private readonly IHostRepository _hostRepository;
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IVantaSyncClient _client;

    public SyncVantaResourcesCommandHandler(
        IVantaSettingsProvider settingsProvider,
        IHostRepository hostRepository,
        IUpgradePathRepository upgradePathRepository,
        IVantaSyncClient client)
    {
        _settingsProvider = settingsProvider;
        _hostRepository = hostRepository;
        _upgradePathRepository = upgradePathRepository;
        _client = client;
    }

    public async Task<VantaSyncResultDto> Handle(SyncVantaResourcesCommand request, CancellationToken cancellationToken)
    {
        var settings = await _settingsProvider.GetAsync(cancellationToken);
        if (!settings.CanSync)
        {
            return new VantaSyncResultDto(
                false, false, 0, 0,
                settings.Enabled
                    ? "The Vanta integration is enabled but not completely configured, so nothing was sent."
                    : "The Vanta integration is switched off.");
        }

        var hosts = await _hostRepository.GetAllAsync(cancellationToken);
        var outdated = await _upgradePathRepository.GetOutdatedStatusesAsync(cancellationToken);
        var snapshot = VantaResourceBuilder.Build(hosts, outdated, settings);

        // Sending an empty component list would tell Vanta every host this server ever reported has
        // ceased to exist, and take every vulnerability recorded against them with it. A fleet that
        // genuinely has no hosts has nothing worth syncing either, so the safe reading of "zero
        // components" is "don't send", not "delete everything" — this is the one guard standing
        // between a query that returns nothing and a wiped compliance inventory.
        //
        // Note the asymmetry: an empty *package* list is sent, and must be. That is how a fleet that
        // has just finished patching clears the vulnerabilities Vanta is still holding for it.
        if (snapshot.Components.Count == 0)
        {
            return new VantaSyncResultDto(
                false, false, 0, 0,
                hosts.Count == 0
                    ? "No hosts are enrolled, so nothing was sent — Vanta keeps whatever it already holds."
                    : "No enrolled host has ever checked in, so nothing was sent — Vanta keeps whatever it already holds.");
        }

        try
        {
            // Components first, and packages only if that succeeded: every package names its
            // component by uniqueId, so packages sent against components Vanta does not yet hold are
            // rejected — or worse, accepted as orphans.
            await _client.SyncVulnerableComponentsAsync(snapshot.Components, cancellationToken);
            await _client.SyncPackageVulnerabilitiesAsync(snapshot.Packages, cancellationToken);
        }
        catch (ExternalServiceException ex)
        {
            // Returned rather than rethrown: both callers (a timer and a button) want the reason
            // displayed, not an unhandled exception — and a Vanta outage must not take down the
            // background service any more than a GitHub outage stops a script being approved.
            return new VantaSyncResultDto(
                true, false, snapshot.Components.Count, snapshot.Packages.Count, ex.Message);
        }

        return new VantaSyncResultDto(
            true, true, snapshot.Components.Count, snapshot.Packages.Count,
            $"Synced {snapshot.Components.Count} host(s) and {snapshot.Packages.Count} outstanding update(s) to Vanta.");
    }
}
