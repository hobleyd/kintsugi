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
RunProgress updateCheckProgress(UpdateCheckStatus status) => RunProgress(
      isRunning: status.isRunning,
      fraction: status.fraction,
      detail: '${status.completed} / ${status.total} checked, ${status.updated} updated, '
          '${status.unchanged} unchanged, ${status.failed} failed',
      summary: '${status.updated} updated, ${status.unchanged} unchanged, ${status.failed} failed.',
      faultReason: status.faultReason,
    );

/// Rewraps a [RunStarted] so a start call reports the shared shape too.
RunStarted<RunProgress> startedProgress<T>(RunStarted<T> started, RunProgress Function(T) map) =>
    RunStarted(started: started.started, status: map(started.status));
