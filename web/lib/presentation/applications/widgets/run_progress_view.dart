import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../core/theme/kintsugi_palette.dart';
import '../../../core/widgets/alert_box.dart';
import '../../../core/widgets/text_bits.dart';
import '../background_run_bloc.dart';

/// One background run's progress bar, and the message it leaves behind.
///
/// Generic over the bloc type so the same widget serves both runs — the two differ only in which
/// bloc they read, which is exactly what the type parameter is for.
class RunProgressView<B extends BackgroundRunBloc> extends StatelessWidget {
  const RunProgressView({super.key, required this.onFinished});

  /// Called once when a watched run finishes, so the screen can re-read the table. This is what
  /// the page this replaces did with `window.location.reload()`.
  final VoidCallback onFinished;

  @override
  Widget build(BuildContext context) => BlocConsumer<B, BackgroundRunState>(
        listenWhen: (previous, current) => !previous.finished && current.finished,
        listener: (context, state) {
          if (state.progress.faultReason == null) onFinished();
        },
        builder: (context, state) {
          final palette = context.palette;

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
                      : Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            const Text('Worth a look:'),
                            const SizedBox(height: 6),
                            // Listed rather than counted: a note exists precisely because a number
                            // would not convey it.
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
