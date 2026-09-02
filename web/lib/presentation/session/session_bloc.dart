import 'dart:async';

import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../core/network/unauthorized_notifier.dart';
import '../../domain/entities/session.dart';
import '../../domain/usecases/session_usecases.dart';

sealed class SessionEvent extends Equatable {
  const SessionEvent();

  @override
  List<Object?> get props => const [];
}

/// Read the bootstrap state. Dispatched once at startup, and again after anything that could
/// change it — saving authentication settings, or a 401 from any other call.
final class SessionRequested extends SessionEvent {
  const SessionRequested();
}

final class SignInRequested extends SessionEvent {
  const SignInRequested({this.returnPath});

  final String? returnPath;

  @override
  List<Object?> get props => [returnPath];
}

final class SignOutRequested extends SessionEvent {
  const SignOutRequested();
}

sealed class SessionState extends Equatable {
  const SessionState();

  @override
  List<Object?> get props => const [];
}

/// Nothing is rendered in this state but a spinner. It is deliberately the initial one: what to
/// show depends entirely on the answer, and guessing would flash the wrong screen — a fresh deploy
/// would see the sign-in page it has no provider for, or the app it is not allowed to use.
final class SessionLoading extends SessionState {
  const SessionLoading();
}

final class SessionReady extends SessionState {
  const SessionReady(this.session);

  final Session session;

  @override
  List<Object?> get props => [session];
}

/// The bootstrap call itself failed — the server is unreachable or answering with something that
/// is not JSON. Distinct from "not signed in", which is a successful answer.
final class SessionUnavailable extends SessionState {
  const SessionUnavailable(this.message);

  final String message;

  @override
  List<Object?> get props => [message];
}

/// Owns the answer to "may this browser use the app, and as whom".
///
/// Every screen sits behind this. It is the client half of what `Program.cs`'s redirecting
/// middleware used to do, and the comment there is worth reading alongside this: the reason it
/// moved is that a static bundle served by nginx never sends its page load to the API to be
/// redirected.
class SessionBloc extends Bloc<SessionEvent, SessionState> {
  SessionBloc({
    required ReadSession readSession,
    required SignIn signIn,
    required SignOut signOut,
    UnauthorizedNotifier? unauthorizedNotifier,
  })  : _readSession = readSession,
        _signIn = signIn,
        _signOut = signOut,
        super(const SessionLoading()) {
    on<SessionRequested>(_onRequested);
    on<SignInRequested>(_onSignIn);
    on<SignOutRequested>(_onSignOut);

    // Any route answering 401 means this browser's session has lapsed, wherever in the app that
    // happened. Re-reading the session is what puts the client back through the same gate a page
    // load would have gone through, and the router then sends it to the sign-in screen.
    _unauthorizedSubscription =
        unauthorizedNotifier?.stream.listen((_) => add(const SessionRequested()));
  }

  final ReadSession _readSession;
  final SignIn _signIn;
  final SignOut _signOut;
  StreamSubscription<void>? _unauthorizedSubscription;

  @override
  Future<void> close() {
    _unauthorizedSubscription?.cancel();
    return super.close();
  }

  Future<void> _onRequested(SessionRequested event, Emitter<SessionState> emit) async {
    try {
      emit(SessionReady(await _readSession()));
    } on UnauthorizedApiException {
      // GET /api/session is anonymous by design, so this should not happen — but the clause has to
      // be here, and ahead of the general one, because UnauthorizedApiException *is* an
      // ApiException. If someone gates this route (the class-level [RequireAdminSession] precedent
      // on every other admin controller makes that a plausible mistake), catching it below would
      // pin the client to the "cannot reach Kintsugi" screen, whose only action re-reads this same
      // route — a retry loop with no way to sign in.
      //
      // A 401 means one thing wherever it comes from: sign in. So it is reported as a session that
      // needs signing into rather than as a broken server. The provider name is unknown in this
      // state, so the sign-in screen falls back to its generic wording.
      emit(const SessionReady(Session(
        authenticationSettingsSaved: true,
        authenticationEnabled: true,
        signedIn: false,
        userName: null,
        providerDisplayName: 'single sign-on',
        canSignIn: true,
        callbackUrl: '',
        signOutCallbackUrl: '',
      )));
    } on ApiException catch (error) {
      emit(SessionUnavailable(error.message));
    }
  }

  // Both of these hand the whole page over to the server, so there is no state to emit and no
  // point awaiting anything: whatever this app is showing is about to be replaced.
  void _onSignIn(SignInRequested event, Emitter<SessionState> emit) =>
      _signIn(returnPath: event.returnPath);

  void _onSignOut(SignOutRequested event, Emitter<SessionState> emit) => _signOut();
}
