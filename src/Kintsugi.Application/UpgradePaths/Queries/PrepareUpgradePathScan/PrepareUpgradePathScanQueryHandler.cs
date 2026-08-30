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

            // Split by how each *variant* is managed rather than by the application as a whole:
            // one name can legitimately be Homebrew-managed on a Mac, winget-managed on one
            // Windows host, and plain-installed on another. Treating the first manager seen as
            // covering every platform (which it used to) meant those other platforms silently got
            // no work item at all, and so no upgrade path ever.
            var managed = group.Where(v => v.ParentApplicationName is not null).ToList();
            var unmanaged = group.Where(v => v.ParentApplicationName is null).ToList();

            foreach (var managerGroup in managed.GroupBy(v => v.ParentApplicationName!, StringComparer.OrdinalIgnoreCase))
            {
                // Package-manager-managed applications resolve to a fixed, server-written script
                // without ever calling the AI (see ResearchApplicationUpgradePathCommandHandler), so
                // they belong in the plan regardless of whether the AI agent is configured — only
                // the Research items below actually need it.
                workItems.Add(new UpgradePathWorkItem(
                    applicationName,
                    PlatformBucket.ForPackageManager(managerGroup.Key),
                    Array.Empty<string>(),
                    UpgradePathWorkKind.PackageManagerManaged,
                    managerGroup.Key,
                    // winget and Chocolatey both address a package by its id, not its display name
                    // — so the identifier is genuinely load-bearing for those, unlike Homebrew where
                    // the package name is the identifier. Falls back to the name for the latter.
                    managerGroup.Select(v => v.ApplicationIdentifier).FirstOrDefault(id => id is not null) ?? applicationName));
            }

            if (unmanaged.Count == 0)
            {
                continue;
            }

            if (packageManagerNames.Contains(applicationName))
            {
                // The application is itself a tracked package manager (e.g. the "Homebrew" or
                // "winget" row) — a manager is its own manager, so its self-update row lives in the
                // very bucket its managed applications do.
                workItems.Add(new UpgradePathWorkItem(
                    applicationName,
                    PlatformBucket.ForPackageManager(applicationName),
                    Array.Empty<string>(),
                    UpgradePathWorkKind.PackageManagerSelfUpdate,
                    applicationName,
                    applicationName));
                continue;
            }

            // Built even when the AI agent isn't configured — ResearchApplicationUpgradePathCommandHandler
            // resolves an item like this to NotFound with an explanatory note rather than attempting
            // a call, the same way it already handles an unrecognized package manager or an
            // unsupported platform. That keeps this plan usable both for the fleet-wide scan (which
            // just wants every application to get *some* resolution attempt) and for a single-row
            // refresh (which needs to find its matching item regardless of AI configuration).
            foreach (var platformGroup in unmanaged.GroupBy(v => PlatformBucket.From(v.OperatingSystem)))
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
