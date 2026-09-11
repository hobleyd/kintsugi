import '../../domain/entities/upgrade_path.dart';
import 'background_run_bloc.dart';

/// Turns the fleet-wide scan's status into the shared [RunProgress] shape.
///
/// The wording is the page's, kept verbatim, because these counts do not mean what a reader would
/// guess: "already known" is work the scan deliberately skipped rather than work it failed at, and
/// "no known path" is a real answer rather than an error.
RunProgress scanProgress(UpgradePathScanStatus status) => RunProgress(
      isRunning: status.isRunning,
      fraction: status.fraction,
      detail: '${status.completed} / ${status.total} checked, ${status.resolved} resolved, '
          '${status.notFound} no known path, ${status.failed} failed, ${status.skipped} already known',
      summary: '${status.resolved} resolved, ${status.notFound} with no known path, '
          '${status.failed} failed, ${status.skipped} already known.',
      faultReason: status.faultReason,
      notes: status.notes,
    );

/// Turns "Check for Updates" into the same shape. Different counts, because this run re-executes
/// each resolved script's own `--update-version` and makes no AI call.
///
/// "nothing to check" is this run's counterpart to the scan's "already known": a row with no
/// script, or none this server can run, is not a row that failed at anything. And the notes matter
/// more here than they do for the scan, because a failed check writes nothing to the row it
/// failed on — the table's "Check Failed" badge belongs to the AI scan — so without them a count
/// of failures names no rows and can be reconciled against nothing on screen.
RunProgress updateCheckProgress(UpdateCheckStatus status) => RunProgress(
      isRunning: status.isRunning,
      fraction: status.fraction,
      detail: '${status.completed} / ${status.total} checked, ${status.updated} updated, '
          '${status.unchanged} unchanged, ${status.failed} failed, '
          '${status.skipped} with nothing to check',
      summary: '${status.updated} updated, ${status.unchanged} unchanged, ${status.failed} failed, '
          '${status.skipped} with nothing to check.',
      faultReason: status.faultReason,
      notes: status.notes,
    );

/// Rewraps a [RunStarted] so a start call reports the shared shape too.
RunStarted<RunProgress> startedProgress<T>(RunStarted<T> started, RunProgress Function(T) map) =>
    RunStarted(started: started.started, status: map(started.status));
