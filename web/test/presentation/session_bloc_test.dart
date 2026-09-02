import 'package:bloc_test/bloc_test.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/network/unauthorized_notifier.dart';
import 'package:kintsugi_web/domain/entities/session.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/session_usecases.dart';
import 'package:kintsugi_web/presentation/session/session_bloc.dart';

class FakeSessionRepository implements SessionRepository {
  FakeSessionRepository({this.session, this.error});

  Session? session;
  ApiException? error;
  int reads = 0;
  bool signedIn = false;
  bool signedOut = false;

  @override
  Future<Session> read() async {
    reads++;
    if (error != null) throw error!;
    return session!;
  }

  @override
  void signIn({String? returnPath}) => signedIn = true;

  @override
  void signOut() => signedOut = true;
}

Session readySession() => const Session(
      authenticationSettingsSaved: true,
      authenticationEnabled: true,
      signedIn: true,
      userName: 'admin@example.com',
      providerDisplayName: 'Google Workspace',
      canSignIn: true,
      callbackUrl: 'https://kintsugi.example.com/signin-oidc',
      signOutCallbackUrl: 'https://kintsugi.example.com/signout-callback-oidc',
    );

SessionBloc blocFor(FakeSessionRepository repository, {UnauthorizedNotifier? notifier}) => SessionBloc(
      readSession: ReadSession(repository),
      signIn: SignIn(repository),
      signOut: SignOut(repository),
      unauthorizedNotifier: notifier,
    );

void main() {
  group('reading the session', () {
    blocTest<SessionBloc, SessionState>(
      'reports what the server said',
      build: () => blocFor(FakeSessionRepository(session: readySession())),
      act: (bloc) => bloc.add(const SessionRequested()),
      expect: () => [SessionReady(readySession())],
    );

    blocTest<SessionBloc, SessionState>(
      'reports an unreachable server as unavailable, not as signed out',
      build: () => blocFor(
        FakeSessionRepository(error: const ApiException('Could not reach the server.')),
      ),
      act: (bloc) => bloc.add(const SessionRequested()),
      expect: () => [const SessionUnavailable('Could not reach the server.')],
    );

    blocTest<SessionBloc, SessionState>(
      'treats a 401 from the bootstrap route as needing sign-in, not as a broken server',
      build: () => blocFor(
        FakeSessionRepository(error: const UnauthorizedApiException('Not signed in.')),
      ),
      act: (bloc) => bloc.add(const SessionRequested()),
      verify: (bloc) {
        // UnauthorizedApiException is an ApiException, so without an explicit clause ahead of the
        // general one this would land on the "cannot reach Kintsugi" screen — whose only action
        // re-reads this same route, giving a retry loop with no way to sign in.
        final state = bloc.state;
        expect(state, isA<SessionReady>());
        final session = (state as SessionReady).session;
        expect(session.signedIn, isFalse);
        expect(session.canSignIn, isTrue);
        expect(
          session.canUseApp,
          isFalse,
          reason: 'the router must send this to the sign-in screen',
        );
      },
    );
  });

  group('a 401 from anywhere else', () {
    test('re-reads the session, so an expired cookie does not become a screen error', () async {
      // The regression this guards against: the Razor UI answered an expired cookie with an
      // unconditional 302. A client that renders a 401 as an error string leaves the operator
      // looking at "Not signed in." above a stale table, with the log-out button hidden because
      // the session it is holding still says signed-in.
      final notifier = UnauthorizedNotifier();
      final repository = FakeSessionRepository(session: readySession());
      final bloc = blocFor(repository, notifier: notifier);

      bloc.add(const SessionRequested());
      await Future<void>.delayed(const Duration(milliseconds: 10));
      expect(repository.reads, 1);

      notifier.notify();
      await Future<void>.delayed(const Duration(milliseconds: 10));
      expect(repository.reads, 2);

      await bloc.close();
      await notifier.dispose();
    });

    test('stops listening once the bloc is closed', () async {
      final notifier = UnauthorizedNotifier();
      final repository = FakeSessionRepository(session: readySession());
      final bloc = blocFor(repository, notifier: notifier);

      await bloc.close();
      notifier.notify();
      await Future<void>.delayed(const Duration(milliseconds: 10));

      expect(repository.reads, 0);
      await notifier.dispose();
    });
  });

  group('sign-in and sign-out', () {
    blocTest<SessionBloc, SessionState>(
      'hand the whole page over rather than emitting a state',
      build: () => blocFor(FakeSessionRepository(session: readySession())),
      act: (bloc) {
        bloc
          ..add(const SignInRequested())
          ..add(const SignOutRequested());
      },
      // Nothing to emit: whatever this app is showing is about to be replaced by the provider.
      expect: () => <SessionState>[],
    );
  });
}
