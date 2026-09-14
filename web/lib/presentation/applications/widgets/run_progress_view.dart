import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../core/diagnostics/diagnostics_log.dart';
import '../../../core/theme/kintsugi_palette.dart';
import '../../../core/widgets/alert_box.dart';
import '../../../core/widgets/text_bits.dart';
import '../background_run_bloc.dart';

/// One background run's progress bar, and the message it leaves behind.
///
/// Generic over the bloc type so the same widget serves both runs — the two differ only in which
/// bloc they read, which is exactly what the type parameter is for.
///
/// **What the run had to say goes to the shell's diagnostics panel, not only to this widget.**
/// A run's notes are a line per row it skipped or could not check, and the next thing anybody
/// does with that list is open Upgrade Scripts or Failed Updates to act on it — which used to
/// destroy the list, because this widget is inside the route. So the finish is recorded into
/// `DiagnosticsLog`, which lives in the shell and survives the navigation. See
/// `core/diagnostics/diagnostics_log.dart`.
class RunProgressView<B extends BackgroundRunBloc> extends StatelessWidget {
  const RunProgressView({super.key, required this.title, required this.onFinished, this.source});

  /// What to call this run in the panel — "Check for Updates". The type parameter identifies the
  /// bloc, not the run, so this cannot be derived from it.
  final String title;

  /// The screen the entry came from, for a panel that is read from other screens.
  final String? source;

  /// Called once when a watched run finishes, so the screen can re-read the table. This is what
  /// the page this replaces did with `window.location.reload()`.
  final VoidCallback onFinished;

  @override
  Widget build(BuildContext context) => BlocConsumer<B, BackgroundRunState>(
        // Both transitions, because both produce output worth keeping: a run that finished, and a
        // run that could not be started at all.
        listenWhen: (previous, current) =>
            (!previous.finished && current.finished) ||
            (current.error != null && current.error != previous.error),
        // Recording notifies listeners, so it has to happen here rather than in `builder` — a
        // notifier fired during a build is an assertion in debug and a rebuild loop without one.
        listener: (context, state) {
          final log = DiagnosticsLogScope.maybeOf(context);

          if (state.error != null) {
            log?.record(
              title: title,
              kind: DiagnosticsKind.error,
              summary: state.error,
              source: source,
            );
            return;
          }

          if (!state.finished) return;

          final fault = state.progress.faultReason;
          log?.record(
            title: title,
            kind: fault == null ? DiagnosticsKind.success : DiagnosticsKind.error,
            summary: fault ?? state.progress.summary,
            source: source,
            // The server bounds this list and says how many rows it left out — see
            // `UpdateCheckCoordinator`. Passed through as given rather than re-bounded here: the
            // panel scrolls, and silently shortening a list that already states its own overflow
            // would make that statement wrong.
            lines: fault == null ? state.progress.notes : const [],
          );

          if (fault == null) onFinished();
        },
        builder: (context, state) {
          final palette = context.palette;
          // Whether anything is collecting. Null off the shell — a widget test pumping this
          // screen alone, most obviously — and the notes are listed inline in that case rather
          // than dropped, because the alternative is a run whose output exists nowhere.
          final collecting = DiagnosticsLogScope.maybeOf(context) != null;

          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (state.progress.isRunning) ...[
                ClipRRect(
                  borderRadius: BorderRadius.circular(3),
                  child: LinearProgressIndicator(
                    value: state.progress.fraction,
                    minHeight: 6,
                    backgroundColor: palette.accentWash(0.08),
                    color: palette.neon,
                  ),
                ),
                const SizedBox(height: 8),
                HintText(state.progress.detail),
                const SizedBox(height: 16),
              ],
              if (state.error != null) AlertBox.error(state.error!),
              if (state.alreadyRunning && state.progress.isRunning)
                const AlertBox.info('A run was already going - showing its progress.'),
              if (state.finished && state.progress.faultReason != null)
                AlertBox.error(state.progress.faultReason!),
              if (state.finished && state.progress.faultReason == null)
                AlertBox.success(
                  state.progress.summary,
                  child: state.progress.notes.isEmpty
                      ? null
                      : collecting
                          // Said rather than listed, because the list is now in the panel that
                          // just slid in beside this one. Repeating it here would put the same
                          // fifty lines on screen twice.
                          ? Text(
                              '${state.progress.notes.length} note(s) worth a look — listed in '
                              'Diagnostics, on the right.',
                            )
                          : Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                const Text('Worth a look:'),
                                const SizedBox(height: 6),
                                // Listed rather than counted: a note exists precisely because a
                                // number would not convey it.
                                for (final note in state.progress.notes)
                                  Padding(
                                    padding: const EdgeInsets.only(bottom: 3),
                                    child: Text('- $note'),
                                  ),
                              ],
                            ),
                ),
            ],
          );
        },
      );
}
