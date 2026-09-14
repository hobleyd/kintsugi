import '../../core/diagnostics/diagnostics_log.dart';
import '../../core/router/app_router.dart';
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

/// Both coordinators write a note as `$"{ApplicationName} ({Platform}): {Note}"` — see
/// `UpdateCheckCoordinator.cs` and `UpgradePathScanCoordinator.cs`, which have that line in common
/// and nothing else. This pulls the application name back out so the diagnostics panel can link
/// the line to the row it is about.
///
/// **The name is matched greedily and the platform is not.** Real application names contain
/// brackets — "Microsoft Visual C++ 2015-2022 Redistributable (x64)" is the shape this exists for
/// — so a lazy name would stop at the first `(` and link to half a name. A platform bucket never
/// contains one, which is what makes the last bracketed group the right one to take.
final _noteSubject = RegExp(r'^(.+) \([^()]*\): ');

/// Turns one note into a panel line, linked to the application it names where it names one.
///
/// **Anything that does not match falls back to plain text, and that is the point of doing it this
/// way.** The overflow line the server appends — "...and N more not listed." — names no row, and a
/// note whose format drifts would produce a link to the wrong screen rather than no link at all.
/// A line that is not obviously about one application is left alone.
DiagnosticsLine noteLine(String note) {
  final name = _noteSubject.firstMatch(note)?.group(1);
  if (name == null || name.isEmpty) return DiagnosticsLine(note);
  return DiagnosticsLine(note, target: Routes.applicationsNamed(name));
}
