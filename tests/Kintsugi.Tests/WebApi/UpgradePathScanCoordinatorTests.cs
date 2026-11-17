using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.ResearchApplicationUpgradePath;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.UpgradePathScanning;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// The row a "Find Upgrade Paths" run keeps for each application, which is what the Diagnostics
/// page splits into Resolved / No known path / Failed / Already known. The status can only count
/// them, and a resolved row usually carries no note at all.
/// </summary>
public class UpgradePathScanCoordinatorTests
{
    private static ResearchApplicationUpgradePathResult Result(UpgradePathStatus status, bool skipped = false, string? note = null) =>
        new("Firefox", "macOS", status, skipped, note);

    [Fact]
    public void GetResults_MapsEachStatusToItsOutcome()
    {
        var coordinator = new UpgradePathScanCoordinator();
        coordinator.TryRequestStart();

        coordinator.ReportItem(Result(UpgradePathStatus.Found));
        coordinator.ReportItem(Result(UpgradePathStatus.NotFound, note: "No upstream source found."));
        coordinator.ReportItem(Result(UpgradePathStatus.Failed, note: "The AI agent returned no script."));
        coordinator.ReportItem(Result(UpgradePathStatus.Found, skipped: true));

        var rows = coordinator.GetResults().Rows;

        Assert.Equal(
            new[] { RunRowOutcome.Resolved, RunRowOutcome.NoKnownPath, RunRowOutcome.Failed, RunRowOutcome.AlreadyKnown },
            rows.Select(r => r.Outcome));
        Assert.Null(rows[0].Note);
        Assert.Equal("No upstream source found.", rows[1].Note);
    }

    [Fact]
    public void GetResults_AgreesWithTheStatusCounts()
    {
        var coordinator = new UpgradePathScanCoordinator();
        coordinator.TryRequestStart();
        coordinator.ReportItem(Result(UpgradePathStatus.Found));
        coordinator.ReportItem(Result(UpgradePathStatus.Found));
        coordinator.ReportItem(Result(UpgradePathStatus.NotFound));
        coordinator.ReportItem(Result(UpgradePathStatus.Failed));
        coordinator.ReportItem(Result(UpgradePathStatus.Found, skipped: true));

        var status = coordinator.GetStatus();
        var rows = coordinator.GetResults().Rows;

        Assert.Equal(status.Resolved, rows.Count(r => r.Outcome == RunRowOutcome.Resolved));
        Assert.Equal(status.NotFound, rows.Count(r => r.Outcome == RunRowOutcome.NoKnownPath));
        Assert.Equal(status.Failed, rows.Count(r => r.Outcome == RunRowOutcome.Failed));
        Assert.Equal(status.Skipped, rows.Count(r => r.Outcome == RunRowOutcome.AlreadyKnown));
    }

    [Fact]
    public void GetResults_IsClearedWhenANewScanStarts()
    {
        var coordinator = new UpgradePathScanCoordinator();
        coordinator.TryRequestStart();
        coordinator.ReportItem(Result(UpgradePathStatus.Found));
        coordinator.Complete();

        coordinator.TryRequestStart();

        Assert.Empty(coordinator.GetResults().Rows);
    }

    [Fact]
    public void GetResults_KeepsAtMostFiveThousandRows_AndCountsTheRest()
    {
        var coordinator = new UpgradePathScanCoordinator();
        coordinator.TryRequestStart();

        for (var i = 0; i < 5002; i++)
        {
            coordinator.ReportItem(Result(UpgradePathStatus.Found));
        }

        var results = coordinator.GetResults();

        Assert.Equal(5000, results.Rows.Count);
        Assert.Equal(2, results.Unlisted);
    }

    [Fact]
    public void Outcome_CrossesTheWireAsItsName()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new RunRowDto("Slack", "macOS", RunRowOutcome.NoKnownPath, null));

        Assert.Contains("\"Outcome\":\"NoKnownPath\"", json);
    }
}
