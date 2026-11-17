using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.CheckApplicationUpdate;
using Kintsugi.WebApi.UpgradePathScanning;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// What the "Check for Updates" progress reports, which is the only account anybody gets of a
/// fleet-wide run.
/// </summary>
/// <remarks>
/// The distinction pinned here is the one <c>UpgradePathScanCoordinator</c> already draws between
/// work that failed and work there was nothing to do. It matters more on this run than on the scan,
/// because a check writes nothing to the row it checked: the Applications table's "Check Failed"
/// badge is <c>UpgradePathStatus.Failed</c>, which only the AI scan ever writes. So a count of
/// failures here names no rows and can be reconciled against nothing on screen — hence the notes,
/// and hence a skip not being billed as a failure.
/// </remarks>
public class UpdateCheckCoordinatorTests
{
    private static CheckApplicationUpdateResult Checked(bool versionChanged) =>
        new("Firefox", "macOS", Success: true, VersionChanged: versionChanged, Note: null);

    private static CheckApplicationUpdateResult Failed(string note) =>
        new("Firefox", "macOS", Success: false, VersionChanged: false, Note: note);

    private static CheckApplicationUpdateResult Skipped() =>
        new("Slack", "pm:Homebrew", Success: false, VersionChanged: false, Note: "No update script to check.", Skipped: true);

    [Fact]
    public void ReportItem_CountsASkippedRowApartFromAFailedOne()
    {
        var coordinator = new UpdateCheckCoordinator();
        Assert.True(coordinator.TryRequestStart());
        coordinator.SetTotal(4);

        coordinator.ReportItem(Checked(versionChanged: true));
        coordinator.ReportItem(Checked(versionChanged: false));
        coordinator.ReportItem(Failed("The script did not report a version."));
        coordinator.ReportItem(Skipped());

        var status = coordinator.GetStatus();

        Assert.Equal(4, status.Completed);
        Assert.Equal(1, status.Updated);
        Assert.Equal(1, status.Unchanged);
        Assert.Equal(1, status.Failed);
        Assert.Equal(1, status.Skipped);
    }

    [Fact]
    public void ReportItem_CollectsTheReasonForEveryRowThatWasNotChecked()
    {
        var coordinator = new UpdateCheckCoordinator();
        coordinator.TryRequestStart();

        coordinator.ReportItem(Checked(versionChanged: false));
        coordinator.ReportItem(Failed("The script did not report a version."));
        coordinator.ReportItem(Skipped());

        var status = coordinator.GetStatus();

        // A successful check contributes nothing: the notes are exactly the rows a reader would go
        // looking for, named the way the scan names its own.
        Assert.Equal(
            new[]
            {
                "Firefox (macOS): The script did not report a version.",
                "Slack (pm:Homebrew): No update script to check.",
            },
            status.Notes);
    }

    [Fact]
    public void ReportItem_StopsListingReasonsAtACeiling_AndSaysHowManyItLeftOut()
    {
        var coordinator = new UpdateCheckCoordinator();
        coordinator.TryRequestStart();

        for (var i = 0; i < 60; i++)
        {
            coordinator.ReportItem(
                new($"App{i}", "pm:Homebrew", Success: false, VersionChanged: false, Note: "No update script to check.", Skipped: true));
        }

        var status = coordinator.GetStatus();

        // The counts still cover every row; only the listing is bounded, and it says so rather
        // than stopping at fifty as though fifty were all there was.
        Assert.Equal(60, status.Skipped);
        Assert.Equal(51, status.Notes.Count);
        Assert.StartsWith("App49 (pm:Homebrew)", status.Notes[49]);
        Assert.Equal("...and 10 more not listed. The counts above cover all of them.", status.Notes[50]);
    }

    [Fact]
    public void TryRequestStart_ClearsTheCountsAndNotesOfThePreviousRun()
    {
        var coordinator = new UpdateCheckCoordinator();
        coordinator.TryRequestStart();
        coordinator.ReportItem(Skipped());
        coordinator.ReportItem(Failed("Unexpected error: subprocess timed out"));
        coordinator.Complete();


        Assert.True(coordinator.TryRequestStart());
        var status = coordinator.GetStatus();

        Assert.Equal(0, status.Completed);
        Assert.Equal(0, status.Failed);
        Assert.Equal(0, status.Skipped);
        Assert.Empty(status.Notes);
    }

    [Fact]
    public void TryRequestStart_RefusesASecondRunWhileOneIsInProgress()
    {
        var coordinator = new UpdateCheckCoordinator();

        Assert.True(coordinator.TryRequestStart());
        Assert.False(coordinator.TryRequestStart());

        coordinator.Complete();

        Assert.True(coordinator.TryRequestStart());
    }
    // The Diagnostics page's tabs are a split by outcome, and the status can only give counts: a
    // successful check carries no note, so the notes can never say which rows were fine.
    [Fact]
    public void GetResults_HasARowPerApplication_WithWhatTheCheckConcludedAboutIt()
    {
        var coordinator = new UpdateCheckCoordinator();
        coordinator.TryRequestStart();

        coordinator.ReportItem(Checked(versionChanged: true));
        coordinator.ReportItem(Checked(versionChanged: false));
        coordinator.ReportItem(Failed("The script did not report a version."));
        coordinator.ReportItem(Skipped());

        var rows = coordinator.GetResults().Rows;

        Assert.Equal(
            new[] { RunRowOutcome.Updated, RunRowOutcome.Unchanged, RunRowOutcome.Failed, RunRowOutcome.NothingToCheck },
            rows.Select(r => r.Outcome));
        Assert.Equal("The script did not report a version.", rows[2].Note);
        Assert.Null(rows[0].Note);
    }

    [Fact]
    public void GetResults_AgreesWithTheStatusCounts()
    {
        var coordinator = new UpdateCheckCoordinator();
        coordinator.TryRequestStart();
        coordinator.ReportItem(Checked(versionChanged: true));
        coordinator.ReportItem(Failed("x"));
        coordinator.ReportItem(Failed("y"));
        coordinator.ReportItem(Skipped());

        var status = coordinator.GetStatus();
        var rows = coordinator.GetResults().Rows;

        Assert.Equal(status.Updated, rows.Count(r => r.Outcome == RunRowOutcome.Updated));
        Assert.Equal(status.Unchanged, rows.Count(r => r.Outcome == RunRowOutcome.Unchanged));
        Assert.Equal(status.Failed, rows.Count(r => r.Outcome == RunRowOutcome.Failed));
        Assert.Equal(status.Skipped, rows.Count(r => r.Outcome == RunRowOutcome.NothingToCheck));
    }

    [Fact]
    public void GetResults_IsClearedWhenANewRunStarts()
    {
        var coordinator = new UpdateCheckCoordinator();
        coordinator.TryRequestStart();
        coordinator.ReportItem(Checked(versionChanged: false));
        coordinator.Complete();

        coordinator.TryRequestStart();

        Assert.Empty(coordinator.GetResults().Rows);
        Assert.Equal(0, coordinator.GetResults().Unlisted);
    }

    [Fact]
    public void GetResults_KeepsAtMostFiveThousandRows_AndCountsTheRest()
    {
        var coordinator = new UpdateCheckCoordinator();
        coordinator.TryRequestStart();

        for (var i = 0; i < 5003; i++)
        {
            coordinator.ReportItem(Checked(versionChanged: false));
        }

        var results = coordinator.GetResults();

        Assert.Equal(5000, results.Rows.Count);
        Assert.Equal(3, results.Unlisted);
        // The status counts still cover every row, which is what "Unlisted" is the difference of.
        Assert.Equal(5003, coordinator.GetStatus().Unchanged);
    }

    [Fact]
    public void GetResults_BeforeAnyRun_IsEmpty()
    {
        Assert.Empty(new UpdateCheckCoordinator().GetResults().Rows);
    }
}
