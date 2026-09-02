import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../domain/entities/upgrade_path.dart';

/// One background run's progress, reduced to what the UI actually draws.
///
/// A shared shape for the two runs — "Find Upgrade Paths" and "Check for Updates" — because they
/// are identical in behaviour and differ only in their outcome counts and their labels. The page
/// this replaces reached the same conclusion and shared one `createRunController` between them.
class RunProgress extends Equatable {
  const RunProgress({
    required this.isRunning,
    required this.fraction,
    required this.detail,
    required this.summary,
    this.faultReason,
    this.notes = const [],
  });

  const RunProgress.idle()
      : isRunning = false,
        fraction = 0,
        detail = '',
        summary = '',
        faultReason = null,
        notes = const [];

  final bool isRunning;
  final double fraction;

  /// The line under the progress bar while the run is going.
  final String detail;

  /// The line shown once it has finished.
  final String summary;

  final String? faultReason;

  /// Things the run wants a human to look at. Listed rather than counted — that is the point of
  /// them.
  final List<String> notes;

  @override
  List<Object?> get props => [isRunning, fraction, detail, summary, faultReason, notes];
}

sealed class BackgroundRunEvent extends Equatable {
  const BackgroundRunEvent();

  @override
  List<Object?> get props => const [];
}

final class RunStartRequested extends BackgroundRunEvent {
  const RunStartRequested();
}

/// A status poll, and also the one-off check on load — a run started from another tab, or before
/// this screen was opened, should show live progress here rather than looking idle.
final class RunStatusRequested extends BackgroundRunEvent {
  const RunStatusRequested({this.adopt = false});

  /// True for the check on load: it treats a run already in progress as this screen's own, so the
  /// completion message and the table reload still happen when it finishes.
  final bool adopt;

  @override
  List<Object?> get props => [adopt];
}

final class RunNoticesDismissed extends BackgroundRunEvent {
  const RunNoticesDismissed();
}

final class BackgroundRunState extends Equatable {
  const BackgroundRunState({
    this.progress = const RunProgress.idle(),
    this.watching = false,
    this.alreadyRunning = false,
    this.finished = false,
    this.error,
  });

  final RunProgress progress;

  /// Whether this screen is following a run it considers its own, which is what decides if the
  /// completion message appears. A run somebody else started in another tab shows its progress but
  /// does not announce its result here.
  final bool watching;

  final bool alreadyRunning;

  /// Set for one state only, when a watched run has just finished — the screen uses it to reload
  /// the table, which is what the old page's `window.location.reload()` was for.
  final bool finished;

  final String? error;

  @override
  List<Object?> get props => [progress, watching, alreadyRunning, finished, error];
}

/// Starts a background run and follows it.
///
/// Parameterised by the two calls and the two text builders rather than subclassed per run,
/// because the differences really are only those four things.
class BackgroundRunBloc extends Bloc<BackgroundRunEvent, BackgroundRunState>
    with Polling<BackgroundRunEvent, BackgroundRunState> {
  BackgroundRunBloc({
    required Future<RunStarted<RunProgress>> Function() start,
    required Future<RunProgress> Function() status,
  })  : _start = start,
        _status = status,
        super(const BackgroundRunState()) {
    on<RunStartRequested>(_onStart);
    on<RunStatusRequested>(_onStatus);
    on<RunNoticesDismissed>(
      (_, emit) => emit(BackgroundRunState(progress: state.progress, watching: state.watching)),
    );
  }

  static const _pollInterval = Duration(seconds: 3);

  final Future<RunStarted<RunProgress>> Function() _start;
  final Future<RunProgress> Function() _status;

  Future<void> _onStart(RunStartRequested event, Emitter<BackgroundRunState> emit) async {
    try {
      final started = await _start();
      emit(BackgroundRunState(
        progress: started.status,
        watching: true,
        alreadyRunning: !started.started,
      ));
      if (started.status.isRunning) {
        startPolling(_pollInterval, const RunStatusRequested());
      } else {
        // Finished before the first poll — a scan with nothing to do, for instance.
        emit(BackgroundRunState(progress: started.status, watching: true, finished: true));
      }
    } on ApiException catch (error) {
      emit(BackgroundRunState(error: 'Could not start: ${error.message}'));
    }
  }

  Future<void> _onStatus(RunStatusRequested event, Emitter<BackgroundRunState> emit) async {
    try {
      final progress = await _status();

      if (progress.isRunning) {
        final watching = state.watching || event.adopt;
        emit(BackgroundRunState(progress: progress, watching: watching));
        if (!isPolling) startPolling(_pollInterval, const RunStatusRequested());
        return;
      }

      stopPolling();

      // Nothing to announce: either no run has happened, or one finished that this screen was not
      // following.
      if (!state.watching) {
        emit(BackgroundRunState(progress: progress));
        return;
      }

      emit(BackgroundRunState(progress: progress, watching: false, finished: true));
    } on ApiException {
      // A dropped poll is transient. Left alone deliberately: replacing live progress with an
      // error because one request in a three-second loop missed would be worse than waiting for
      // the next one.
    }
  }
}
