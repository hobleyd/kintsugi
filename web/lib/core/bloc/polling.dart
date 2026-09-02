import 'dart:async';

import 'package:bloc/bloc.dart';

/// Drives a repeating event into a bloc.
///
/// Polling is how this UI stays current, and that is a deliberate choice rather than a shortcut:
/// the three background coordinators on the server already expose their progress as `*-status`
/// routes designed to be polled, so there is no push channel to consume and adding one would be
/// new protocol for a UI that only ever reads. What changed relative to the pages this replaced is
/// what happens with the answer — a poll now emits a state and the affected widgets rebuild,
/// instead of the old `window.location.reload()`.
///
/// Mixed into a bloc rather than written out in each one so the thing that is easy to get wrong is
/// only written once: a timer that outlives its bloc.
mixin Polling<Event, State> on Bloc<Event, State> {
  Timer? _timer;

  /// Starts polling, replacing any interval already running.
  ///
  /// Restarting is idempotent by design: a screen calls this when a background run begins and
  /// again on every state change while it is going, and the alternative — a guard that ignores the
  /// second call — leaves a stopped timer looking started.
  void startPolling(Duration interval, Event event) {
    _timer?.cancel();
    _timer = Timer.periodic(interval, (_) {
      if (!isClosed) add(event);
    });
  }

  void stopPolling() {
    _timer?.cancel();
    _timer = null;
  }

  bool get isPolling => _timer != null;

  @override
  Future<void> close() {
    _timer?.cancel();
    return super.close();
  }
}
