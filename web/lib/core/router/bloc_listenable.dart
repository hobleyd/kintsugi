import 'dart:async';

import 'package:flutter/foundation.dart';

/// Bridges a bloc's state stream to the [Listenable] `go_router` wants for `refreshListenable`.
///
/// Needed because routing here depends on session state — a fresh deploy is pinned to the
/// Authentication screen, and an unauthenticated visitor to the sign-in screen — and `go_router`
/// re-evaluates its redirect when a listenable notifies rather than by watching a stream.
class BlocListenable extends ChangeNotifier {
  BlocListenable(Stream<Object?> stream) {
    _subscription = stream.listen((_) => notifyListeners());
  }

  late final StreamSubscription<Object?> _subscription;

  @override
  void dispose() {
    _subscription.cancel();
    super.dispose();
  }
}
