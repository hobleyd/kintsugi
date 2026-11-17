using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.DeviceHealth;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Autonomy;

/// <summary>What the cause analysis and the KPIs are worked out from, read once and the same way for the
/// page, the daily snapshot and Patch now by cause.</summary>
public record AutonomyInputs(
    IReadOnlyList<Host> Hosts,
    IReadOnlyList<PatchFailureDto> OpenFailures,
    IReadOnlyDictionary<Guid, int> AppUpdates,
    IReadOnlyList<HostHealthAssessment> Health)
{
    public static async Task<AutonomyInputs> LoadAsync(
        IHostRepository hosts,
        IPatchFailureRepository failures,
        IUpgradePathRepository upgradePaths,
        CancellationToken cancellationToken,
        IHostEventRepository? events = null)
    {
        var all = await hosts.GetAllAsync(cancellationToken);
        var ids = all.Select(h => h.Id).ToHashSet();
        var open = (await failures.GetAllAsync(cancellationToken))
            .Where(f => f.Resolution == PatchFailureResolution.Outstanding && ids.Contains(f.HostId))
            .ToList();
        var statuses = await upgradePaths.GetInstallationPatchStatusesAsync(cancellationToken);
        var appUpdates = statuses
            .Where(s => s.UpdateAvailable == true)
            .GroupBy(s => s.HostId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Only what the page and the daily snapshot need: Patch now and the paged devices do not read health.
        IReadOnlyList<HostHealthAssessment> health = [];
        if (events is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var known = all.Select(h => h.Id).ToHashSet();
            health = HostHealthAssessor
                .AssessAll((await events.GetHealthEventsSinceAsync(now.AddDays(-30), cancellationToken)).Where(e => known.Contains(e.HostId)), now);
        }

        return new AutonomyInputs(all, open, appUpdates, health);
    }

    public IReadOnlyList<CauseEntry> Causes(DateTimeOffset now) => AutonomyCauseAnalyzer.Analyze(Hosts, OpenFailures, AppUpdates, now);
}
