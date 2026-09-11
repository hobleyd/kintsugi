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
}
