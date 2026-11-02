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
}
