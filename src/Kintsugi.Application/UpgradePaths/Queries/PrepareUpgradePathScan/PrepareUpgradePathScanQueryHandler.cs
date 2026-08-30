using MediatR;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.PrepareUpgradePathScan;

public class PrepareUpgradePathScanQueryHandler : IRequestHandler<PrepareUpgradePathScanQuery, UpgradePathScanPlan>
{
    private readonly IAiAgentSettingsRepository _aiAgentSettingsRepository;
    private readonly IInstalledApplicationRepository _installedApplicationRepository;

    public PrepareUpgradePathScanQueryHandler(
        IAiAgentSettingsRepository aiAgentSettingsRepository,
        IInstalledApplicationRepository installedApplicationRepository)
    {
        _aiAgentSettingsRepository = aiAgentSettingsRepository;
        _installedApplicationRepository = installedApplicationRepository;
    }

    public async Task<UpgradePathScanPlan> Handle(PrepareUpgradePathScanQuery request, CancellationToken cancellationToken)
    {
        var settings = await _aiAgentSettingsRepository.GetAsync(cancellationToken);
        var aiConfigured = settings is not null && settings.IsEnabled;

        var variants = await _installedApplicationRepository.GetApplicationVersionVariantsAsync(cancellationToken);
        var byName = variants.GroupBy(v => v.ApplicationName, StringComparer.OrdinalIgnoreCase).ToList();

        var packageManagerNames = variants
            .Where(v => v.ParentApplicationName is not null)
            .Select(v => v.ParentApplicationName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var workItems = new List<UpgradePathWorkItem>();

        foreach (var group in byName)
        {
            var applicationName = group.Key;
            var managedBy = group.Select(v => v.ParentApplicationName).FirstOrDefault(p => p is not null);

            if (managedBy is not null)
            {
                // Package-manager-managed applications resolve to a fixed command without ever
                // calling the AI (see ResearchApplicationUpgradePathCommandHandler), so they belong
                // in the plan regardless of whether the AI agent is configured — only the Research
                // items below actually need it.
                workItems.Add(new UpgradePathWorkItem(applicationName, PlatformBucket.Generic, Array.Empty<string>(), UpgradePathWorkKind.PackageManagerManaged, managedBy));
                continue;
            }

            if (packageManagerNames.Contains(applicationName))
            {
                // The application is itself a tracked package manager (e.g. the "Homebrew" row).
                workItems.Add(new UpgradePathWorkItem(applicationName, PlatformBucket.Generic, Array.Empty<string>(), UpgradePathWorkKind.PackageManagerSelfUpdate, applicationName));
                continue;
            }

            // Built even when the AI agent isn't configured — ResearchApplicationUpgradePathCommandHandler
            // resolves an item like this to NotFound with an explanatory note rather than attempting
            // a call, the same way it already handles an unrecognized package manager or an
            // unsupported platform. That keeps this plan usable both for the fleet-wide scan (which
            // just wants every application to get *some* resolution attempt) and for a single-row
            // refresh (which needs to find its matching item regardless of AI configuration).
            foreach (var platformGroup in group.GroupBy(v => PlatformBucket.From(v.OperatingSystem)))
            {
                var knownVersions = platformGroup.Select(v => v.Version).Distinct().ToList();
                var applicationIdentifier = platformGroup.Select(v => v.ApplicationIdentifier).FirstOrDefault(id => id is not null);
                workItems.Add(new UpgradePathWorkItem(applicationName, platformGroup.Key, knownVersions, UpgradePathWorkKind.Research, null, applicationIdentifier));
            }
        }

        var providerSettings = aiConfigured ? new AiProviderSettings(settings!.Provider, settings.ApiKey, settings.BaseUrl, settings.Model) : null;
        return new UpgradePathScanPlan(aiConfigured, providerSettings, workItems);
    }
}
