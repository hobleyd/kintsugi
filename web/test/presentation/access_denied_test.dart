import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/domain/entities/session.dart';
import 'package:kintsugi_web/presentation/session/access_denied_screen.dart';
import 'package:kintsugi_web/presentation/session/session_bloc.dart';

import 'session_bloc_test.dart';

/// Someone signed in with an account that is not on the administrators list sees why, and can
/// sign out — rather than the sign-in screen, which would send them round the same loop.
void main() {
  const denied = Session(
    authenticationSettingsSaved: true,
    authenticationEnabled: true,
    signedIn: true,
    authorized: false,
    userName: 'someone.else',
    providerDisplayName: 'Google Workspace',
    canSignIn: true,
    callbackUrl: '',
    signOutCallbackUrl: '',
  );

  const deniedWithEmail = Session(
    authenticationSettingsSaved: true,
    authenticationEnabled: true,
    signedIn: true,
    authorized: false,
    userName: 'jake',
    email: 'jake@gmail.com',
    providerDisplayName: 'Google Workspace',
    canSignIn: true,
    callbackUrl: '',
    signOutCallbackUrl: '',
  );

  testWidgets('shows the full email, so a wrong-account sign-in is visible', (tester) async {
    final bloc = blocFor(FakeSessionRepository(session: deniedWithEmail))..add(const SessionRequested());

    await tester.pumpWidget(
      MaterialApp(
        home: BlocProvider.value(value: bloc, child: const AccessDeniedScreen()),
      ),
    );
    await tester.pumpAndSettle();

    // "jake is not an administrator" would not say it was jake@gmail.com rather than jake@htw.com.au.
    expect(find.textContaining('jake@gmail.com is not an administrator'), findsOneWidget);
  });

  testWidgets('names the account and offers to sign out', (tester) async {
    final repository = FakeSessionRepository(session: denied);
    final bloc = blocFor(repository)..add(const SessionRequested());

    await tester.pumpWidget(
      MaterialApp(
        home: BlocProvider.value(value: bloc, child: const AccessDeniedScreen()),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('ACCESS DENIED'), findsOneWidget);
    // No email on this session (a server that predates the field, or one the provider has not
    // verified), so it falls back to the display name.
    expect(find.textContaining('someone.else is not an administrator'), findsOneWidget);

    await tester.tap(find.text('SIGN OUT'));
    await tester.pump();

    expect(repository.signedOut, isTrue);
  });
}
