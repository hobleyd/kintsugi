using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Baselines;

/// <summary>
/// The compliance numbers: deterministic counting over what hosts reported, with no model involved
/// (the design keeps "the compliance numbers" on the deterministic side on purpose).
/// </summary>
public static class BaselineCompliance
{
    /// <summary>The version agents are given: the highest-numbered <em>signed</em> one. A newer unsigned
    /// draft never displaces it.</summary>
    public static BaselineVersion? ServedVersion(IEnumerable<BaselineVersion> versions) =>
        versions.Where(v => v.IsSigned).MaxBy(v => v.Number);

    /// <summary>The live hosts <paramref name="baseline"/> applies to.</summary>
    public static IReadOnlyList<Host> ApplicableHosts(Baseline baseline, IEnumerable<Host> hosts) =>
        hosts.Where(h => h.DeletedAtUtc is null && baseline.AppliesTo(h.Id, IsWindows(h))).ToList();

    public static bool IsWindows(Host host) => PlatformBucket.From(host.OperatingSystem) == PlatformBucket.Windows;

    /// <summary>
    /// Per item, how many of <paramref name="applicableHostIds"/> are in each state for it. Only rows for
    /// those hosts count — a host taken off the baseline drops out of the numbers rather than being
    /// counted for ever on its last report — and each host counts once per item.
    /// </summary>
    public static IReadOnlyList<BaselineItemComplianceDto> PerItem(
        IReadOnlyList<BaselineItem> items,
        IReadOnlyCollection<Guid> applicableHostIds,
        IEnumerable<HostBaselineItemState> states)
    {
        var hostIds = applicableHostIds.ToHashSet();
        var byKey = states
            .Where(s => hostIds.Contains(s.HostId))
            .GroupBy(s => s.ItemKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return items.Select(item =>
        {
            if (!item.IsSetting)
            {
                return new BaselineItemComplianceDto(item, new ComplianceCountsDto(0, 0, 0, 0, 0));
            }

            var reported = byKey.TryGetValue(item.Key, out var rows) ? rows : new List<HostBaselineItemState>();
            return new BaselineItemComplianceDto(item, Count(reported.Select(r => r.State), hostIds.Count));
        }).ToList();
    }

    /// <summary>Per host, how many of the version's settings are in each state.</summary>
    public static IReadOnlyList<BaselineHostComplianceDto> PerHost(
        IReadOnlyList<BaselineItem> items,
        IReadOnlyList<Host> applicableHosts,
        IEnumerable<HostBaselineItemState> states)
    {
        var settingKeys = items.Where(i => i.IsSetting).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var byHost = states
            .Where(s => settingKeys.Contains(s.ItemKey))
            .GroupBy(s => s.HostId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return applicableHosts
            .Select(host =>
            {
                var rows = byHost.TryGetValue(host.Id, out var found) ? found : new List<HostBaselineItemState>();
                return new BaselineHostComplianceDto(
                    host.Id,
                    host.Hostname,
                    Count(rows.Select(r => r.State), settingKeys.Count),
                    rows.Count == 0 ? null : rows.Max(r => r.LastCheckedUtc));
            })
            .OrderBy(h => h.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ComplianceCountsDto Sum(IEnumerable<ComplianceCountsDto> counts) =>
        counts.Aggregate(
            new ComplianceCountsDto(0, 0, 0, 0, 0),
            (total, c) => new ComplianceCountsDto(
                total.Compliant + c.Compliant,
                total.Drifted + c.Drifted,
                total.NotApplicable + c.NotApplicable,
                total.Error + c.Error,
                total.NotReported + c.NotReported));

    private static ComplianceCountsDto Count(IEnumerable<BaselineItemState> states, int expected)
    {
        var list = states.ToList();
        return new ComplianceCountsDto(
            list.Count(s => s == BaselineItemState.Compliant),
            list.Count(s => s == BaselineItemState.Drifted),
            list.Count(s => s == BaselineItemState.NotApplicable),
            list.Count(s => s == BaselineItemState.Error),
            Math.Max(0, expected - list.Count));
    }
}
