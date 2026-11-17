using Kintsugi.Application.Baselines;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using static Kintsugi.Tests.Application.Baselines.BaselineTestData;

namespace Kintsugi.Tests.Application.Baselines;

public class BaselineComplianceTests
{
    private static HostBaselineItemState State(Host host, Guid version, string key, BaselineItemState state)
    {
        var row = HostBaselineItemState.Create(host.Id, version, key, DateTimeOffset.UtcNow);
        row.Record(state, "1", null, DateTimeOffset.UtcNow);
        return row;
    }

    [Fact]
    public void PerItem_CountsEachStateAndWhoHasNotReported_OnlyForHostsTheBaselineAppliesTo()
    {
        var version = Guid.NewGuid();
        var (a, b, c, gone) = (new Host("a", "A", "Windows 11"), new Host("b", "B", "Windows 11"), new Host("c", "C", "Windows 11"), new Host("d", "D", "Windows 11"));
        var items = new[] { Setting("copilot.off"), Setting("store.off", valueName: "Store"), Fact("edition") };
        var states = new[]
        {
            State(a, version, "copilot.off", BaselineItemState.Compliant),
            State(b, version, "copilot.off", BaselineItemState.Drifted),
            State(a, version, "store.off", BaselineItemState.Error),
            // A host no longer assigned: its last report must not count for ever.
            State(gone, version, "copilot.off", BaselineItemState.Drifted),
        };

        var perItem = BaselineCompliance.PerItem(items, new[] { a.Id, b.Id, c.Id }, states);

        Assert.Equal(new ComplianceCountsDto(1, 1, 0, 0, 1), perItem.Single(i => i.Item.Key == "copilot.off").Counts);
        Assert.Equal(new ComplianceCountsDto(0, 0, 0, 1, 2), perItem.Single(i => i.Item.Key == "store.off").Counts);
        // Facts are reported, never judged.
        Assert.Equal(new ComplianceCountsDto(0, 0, 0, 0, 0), perItem.Single(i => i.Item.Key == "edition").Counts);
        Assert.Equal(new ComplianceCountsDto(1, 1, 0, 1, 3), BaselineCompliance.Sum(perItem.Select(i => i.Counts)));

        var perHost = BaselineCompliance.PerHost(items, new[] { a, b, c }, states);
        Assert.Equal(new ComplianceCountsDto(1, 0, 0, 1, 0), perHost.Single(h => h.Hostname == "a").Counts);
        Assert.Equal(new ComplianceCountsDto(0, 0, 0, 0, 2), perHost.Single(h => h.Hostname == "c").Counts);
        Assert.Null(perHost.Single(h => h.Hostname == "c").LastCheckedUtc);
    }

    [Fact]
    public void ServedVersion_IsTheNewestSignedOne_NeverANewerDraft()
    {
        var baseline = Guid.NewGuid();
        BaselineVersion V(int n, bool signed)
        {
            var v = BaselineVersion.Create(Guid.NewGuid(), baseline, n, "{}", "hash", null);
            if (signed) v.Sign("sig", null, DateTimeOffset.UtcNow);
            return v;
        }

        Assert.Equal(2, BaselineCompliance.ServedVersion(new[] { V(1, true), V(2, true), V(3, false) })!.Number);
        Assert.Null(BaselineCompliance.ServedVersion(new[] { V(1, false) }));
    }

    [Fact]
    public void ApplicableHosts_IsEveryWindowsHostOrTheNamedOnes()
    {
        var (win, mac) = (new Host("win", "W", "Windows 11 Pro"), new Host("mac", "M", "macOS 15.0"));
        var baseline = Baseline.Create("b", null);

        baseline.Assign(true, Array.Empty<Guid>(), null);
        Assert.Equal(new[] { "win" }, BaselineCompliance.ApplicableHosts(baseline, new[] { win, mac }).Select(h => h.Hostname));

        baseline.Assign(false, new[] { mac.Id }, null);
        Assert.Equal(new[] { "mac" }, BaselineCompliance.ApplicableHosts(baseline, new[] { win, mac }).Select(h => h.Hostname));
    }
}
