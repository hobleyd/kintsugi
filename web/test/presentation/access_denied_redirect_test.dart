import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/router/app_router.dart';
import 'package:kintsugi_web/domain/entities/session.dart';
import 'package:kintsugi_web/presentation/session/session_bloc.dart';

import 'session_bloc_test.dart';

/// Where `createRouter`'s redirect sends each kind of caller. This is the logic that decides
/// whether a signed-in account that is not an administrator ever sees the Access denied screen, so
/// it is tested through the real router rather than by re-stating the rule.
void main() {
  Session session({required bool signedIn, required bool authorized, bool enabled = true}) =>
      Session(
        authenticationSettingsSaved: true,
        authenticationEnabled: enabled,
        signedIn: signedIn,
        authorized: authorized,
        userName: signedIn ? 'someone' : null,
        providerDisplayName: 'Google Workspace',
        canSignIn: true,
        callbackUrl: '',
        signOutCallbackUrl: '',
      );

  /// Resolves [path] for a caller in [state] and returns where the router ends up.
  Future<String> resolve(WidgetTester tester, Session state, String path) async {
    final bloc = blocFor(FakeSessionRepository(session: state));
    bloc.add(const SessionRequested());
    await tester.runAsync(() => bloc.stream.firstWhere((s) => s is SessionReady));

    final router = createRouter(bloc);
    await tester.pumpWidget(const SizedBox());
    final context = tester.element(find.byType(SizedBox));
    final matches = await router.routeInformationParser.parseRouteInformationWithDependencies(
      RouteInformation(uri: Uri.parse(path)),
      context,
    );
    return matches.uri.path;
  }

  testWidgets('a signed-in account that is not an administrator is sent to Access denied',
      (tester) async {
    final denied = session(signedIn: true, authorized: false);

    expect(await resolve(tester, denied, Routes.hosts), Routes.accessDenied);
    expect(await resolve(tester, denied, Routes.signIn), Routes.accessDenied);
    expect(await resolve(tester, denied, Routes.accessDenied), Routes.accessDenied);
  });

  testWidgets('someone who is not signed in is sent to sign-in, not to Access denied',
      (tester) async {
    final signedOut = session(signedIn: false, authorized: false);

    expect(await resolve(tester, signedOut, Routes.hosts), Routes.signIn);
    expect(await resolve(tester, signedOut, Routes.accessDenied), Routes.signIn);
  });

  testWidgets('an administrator is not left on Access denied', (tester) async {
    final admin = session(signedIn: true, authorized: true);

    expect(await resolve(tester, admin, Routes.accessDenied), Routes.hosts);
    expect(await resolve(tester, admin, Routes.hosts), Routes.hosts);
  });

  testWidgets('with sign-in switched off nobody is denied, whatever authorized says',
      (tester) async {
    final open = session(signedIn: true, authorized: false, enabled: false);

    expect(await resolve(tester, open, Routes.hosts), Routes.hosts);
    expect(await resolve(tester, open, Routes.accessDenied), Routes.hosts);
  });

  group('a page load held on the splash remembers where it was headed', () {
    /// The router for a session that has not answered yet, and where it sends [path].
    Future<Uri> whileLoading(WidgetTester tester, String path) async {
      final bloc = blocFor(FakeSessionRepository(session: session(signedIn: true, authorized: true)));
      final router = createRouter(bloc);
      await tester.pumpWidget(const SizedBox());
      final context = tester.element(find.byType(SizedBox));
      final matches = await router.routeInformationParser.parseRouteInformationWithDependencies(
        RouteInformation(uri: Uri.parse(path)),
        context,
      );
      return matches.uri;
    }

    /// Where an allowed caller is sent from the splash that was asked to return to [from].
    Future<String> afterLoading(WidgetTester tester, String from, {Session? as}) =>
        resolve(tester, as ?? session(signedIn: true, authorized: true), Routes.startingThen(from));

    testWidgets('holds the address on the splash instead of forgetting it', (tester) async {
      final uri = await whileLoading(tester, '/diagnostics');

      expect(uri.path, Routes.starting);
      expect(uri.queryParameters['from'], '/diagnostics');
    });

    testWidgets('keeps the query of the address it holds', (tester) async {
      final uri = await whileLoading(tester, '/applications?search=Slack');

      expect(uri.queryParameters['from'], '/applications?search=Slack');
    });

    testWidgets('returns to it once the session is ready', (tester) async {
      expect(await afterLoading(tester, '/diagnostics'), '/diagnostics');
      expect(await afterLoading(tester, '/applications?search=Slack'), '/applications');
    });

    testWidgets('goes to Hosts when it was not headed anywhere', (tester) async {
      expect(await resolve(tester, session(signedIn: true, authorized: true), Routes.starting), Routes.hosts);
      expect(await afterLoading(tester, '/'), Routes.hosts);
    });

    testWidgets('does not follow an address that leaves the app', (tester) async {
      for (final from in ['//evil.example/x', 'https://evil.example/x', r'/\evil.example', 'diagnostics', '']) {
        expect(await afterLoading(tester, from), Routes.hosts, reason: from);
      }
    });

    testWidgets('does not return to a gate screen, which would only bounce back', (tester) async {
      for (final from in [Routes.signIn, Routes.accessDenied, Routes.unavailable, Routes.starting]) {
        expect(await afterLoading(tester, from), Routes.hosts, reason: from);
      }
    });

    testWidgets('still sends someone who is not allowed in to Access denied, whatever they asked for', (tester) async {
      expect(
        await afterLoading(tester, '/diagnostics', as: session(signedIn: true, authorized: false)),
        Routes.accessDenied,
      );
    });

    testWidgets('still sends someone who is not signed in to sign-in', (tester) async {
      expect(
        await afterLoading(tester, '/diagnostics', as: session(signedIn: false, authorized: false)),
        Routes.signIn,
      );
    });
  });
}
