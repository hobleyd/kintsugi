import 'dart:async';

/// Announces that the server has stopped accepting this browser's session.
///
/// This exists because of a regression the move away from server-rendered pages introduced, and it
/// is worth stating plainly so it does not come back. When the UI was Razor Pages, an expired
/// cookie was answered by an unconditional 302 to the sign-in page — the operator could not miss
/// it. A client that only ever reads JSON gets a 401 instead, and if each screen simply renders it
/// as an error string, an expired session looks like "Not signed in." printed above a stale table
/// with no way to do anything about it. Worse, the sidebar's log-out button is hidden in that
/// state, because the session the client is holding still says it is signed in.
///
/// So [ApiClient] raises this on any 401, once, from the one layer that can tell a 401 from an
/// ordinary failure, and [SessionBloc] re-reads `GET /api/session` when it does — which routes to
/// the sign-in screen through exactly the same gate a fresh page load would have used. Screen blocs
/// stay unaware of it, which is the point: every one of them would otherwise need the same clause.
class UnauthorizedNotifier {
  final _controller = StreamController<void>.broadcast();

  Stream<void> get stream => _controller.stream;

  void notify() {
    if (!_controller.isClosed) _controller.add(null);
  }

  Future<void> dispose() => _controller.close();
}
