namespace Kintsugi.Application.UpgradePaths;

/// <summary>Live progress of the current (or most recently completed) "Check for Updates" run —
/// re-running each existing AI-generated script's own <c>--update-version</c> mode against every
/// resolved script upgrade path, with no AI call involved.</summary>
/// <param name="Skipped">
/// Rows the run declined to check rather than failed at — no script, or no
/// <c>ApplicationIdentifier</c> to run one with. Counted apart from <paramref name="Failed"/> for
/// the same reason <see cref="UpgradePathScanStatusDto"/> keeps its own skipped count apart:
/// a count of failures is read as a count of things that are broken. Billing these as failures is
/// what produced a run reporting "8 failed" against a table in which no row wore "Check Failed" —
/// that badge is <see cref="UpgradePathStatusKey.CheckFailed"/>, which only the AI scan's
/// <c>UpgradePathStatus.Failed</c> ever produces, and this run writes no status at all.
/// </param>
/// <param name="Notes">
/// Why each row was skipped or failed, one line apiece, as
/// <c>ApplicationName (Platform): reason</c>. Listed rather than counted, like the scan's — a
/// number cannot be reconciled against the table, and these reasons were already being computed
/// per row and thrown away.
/// </param>
public record UpdateCheckStatusDto(
    bool IsRunning,
    int Total,
    int Completed,
    int Updated,
    int Unchanged,
    int Failed,
    int Skipped,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FaultReason,
    IReadOnlyList<string> Notes);
