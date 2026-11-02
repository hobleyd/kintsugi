import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/settings_usecases.dart';
import 'package:kintsugi_web/presentation/settings/authentication_screen.dart';

import 'session_bloc_test.dart';

class _FakeAuthenticationRepository implements AuthenticationSettingsRepository {
  _FakeAuthenticationRepository({this.stored, this.readFailure});

  final AuthenticationSettings? stored;
  final ApiException? readFailure;

  /// One entry per PUT: the `adminEmails` that would have gone on the wire.
  final sentAdminEmails = <List<String>?>[];

  @override
  Future<AuthenticationSettings> read() async {
    if (readFailure != null) throw readFailure!;
    return stored!;
  }

  @override
  Future<AuthenticationSettings> update({
    required AuthProvider provider,
    required String? clientId,
    required String? clientSecret,
    required String? authority,
    required String? tenantId,
    required String? hostedDomain,
    required bool isEnabled,
    required List<String>? adminEmails,
  }) async {
    sentAdminEmails.add(adminEmails);
    return stored!;
  }
}

AuthenticationSettings _settings({required bool isEnabled, required List<String> adminEmails}) =>
    AuthenticationSettings(
      provider: AuthProvider.googleWorkspace,
      clientId: 'client',
      authority: null,
      tenantId: null,
      hostedDomain: null,
      isEnabled: isEnabled,
      hasClientSecret: true,
      adminEmails: adminEmails,
    );

/// What Jake's review of the administrators field found: a failed load left an empty form that
/// could still be saved — sending `adminEmails: []`, which the server reads as "clear the list" —
/// and clearing a list by hand gave no hint that it hands the site to every account that can sign
/// in. `UpdateAuthenticationSettingsCommand` is the other end: null keeps the stored list.
void main() {
  tearDown(() => locator.reset());

  Future<_FakeAuthenticationRepository> pumpScreen(
    WidgetTester tester,
    _FakeAuthenticationRepository repository,
  ) async {
    locator
      ..registerSingleton(GetAuthenticationSettings(repository))
      ..registerSingleton(UpdateAuthenticationSettings(repository));

    tester.view.physicalSize = const Size(1400, 1600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: BlocProvider(
          create: (_) => blocFor(FakeSessionRepository(session: readySession())),
          child: const Scaffold(body: AuthenticationSettingsScreen()),
        ),
      ),
    );
    await tester.pumpAndSettle();
    return repository;
  }

  final adminField = find.byWidgetPredicate(
    (widget) => widget is TextField && widget.decoration?.hintText == 'name@example.com',
  );
  final saveButton = find.widgetWithText(FilledButton, 'SAVE SETTINGS');

  group('a failed load', () {
    testWidgets('leaves Save disabled, so the blank form cannot overwrite the server', (tester) async {
      final repository = await pumpScreen(
        tester,
        _FakeAuthenticationRepository(readFailure: const ApiException('Could not reach the server.')),
      );

      expect(find.text('Could not reach the server.'), findsOneWidget);
      expect(tester.widget<FilledButton>(saveButton).onPressed, isNull);

      await tester.tap(saveButton, warnIfMissed: false);
      await tester.pumpAndSettle();
      expect(repository.sentAdminEmails, isEmpty);
    });

    testWidgets('does not disable Save once the settings have loaded', (tester) async {
      final repository = await pumpScreen(
        tester,
        _FakeAuthenticationRepository(stored: _settings(isEnabled: false, adminEmails: ['a@example.com'])),
      );

      expect(tester.widget<FilledButton>(saveButton).onPressed, isNotNull);

      await tester.tap(saveButton);
      await tester.pumpAndSettle();
      // Loaded, so the list the server reported goes back rather than null.
      expect(repository.sentAdminEmails, [
        ['a@example.com'],
      ]);
    });
  });

  group('clearing the administrators list', () {
    testWidgets('asks first while sign-in is required, and saves only once confirmed', (tester) async {
      final repository = await pumpScreen(
        tester,
        _FakeAuthenticationRepository(stored: _settings(isEnabled: true, adminEmails: ['a@example.com'])),
      );

      await tester.enterText(adminField, '');
      await tester.tap(saveButton);
      await tester.pumpAndSettle();

      expect(find.text('Clear the administrators list?'), findsOneWidget);
      expect(find.textContaining('every account that can sign in will become an administrator'), findsOneWidget);
      expect(repository.sentAdminEmails, isEmpty);

      await tester.tap(find.text('CLEAR LIST'));
      await tester.pumpAndSettle();

      expect(repository.sentAdminEmails, [<String>[]]);
    });

    testWidgets('saves nothing when the dialog is cancelled', (tester) async {
      final repository = await pumpScreen(
        tester,
        _FakeAuthenticationRepository(stored: _settings(isEnabled: true, adminEmails: ['a@example.com'])),
      );

      await tester.enterText(adminField, '  \n ');
      await tester.tap(saveButton);
      await tester.pumpAndSettle();
      await tester.tap(find.text('CANCEL'));
      await tester.pumpAndSettle();

      expect(find.text('Clear the administrators list?'), findsNothing);
      expect(repository.sentAdminEmails, isEmpty);
    });

    testWidgets('does not ask when sign-in is not required', (tester) async {
      final repository = await pumpScreen(
        tester,
        _FakeAuthenticationRepository(stored: _settings(isEnabled: false, adminEmails: ['a@example.com'])),
      );

      await tester.enterText(adminField, '');
      await tester.tap(saveButton);
      await tester.pumpAndSettle();

      expect(find.text('Clear the administrators list?'), findsNothing);
      expect(repository.sentAdminEmails, [<String>[]]);
    });

    testWidgets('does not ask when the list was already empty', (tester) async {
      final repository = await pumpScreen(
        tester,
        _FakeAuthenticationRepository(stored: _settings(isEnabled: true, adminEmails: const [])),
      );

      await tester.tap(saveButton);
      await tester.pumpAndSettle();

      expect(find.text('Clear the administrators list?'), findsNothing);
      expect(repository.sentAdminEmails, [<String>[]]);
    });
  });

  group('pasting addresses', () {
    Future<List<String>?> savedFrom(WidgetTester tester, String pasted) async {
      final repository = await pumpScreen(
        tester,
        _FakeAuthenticationRepository(stored: _settings(isEnabled: false, adminEmails: const [])),
      );

      await tester.enterText(adminField, pasted);
      await tester.tap(saveButton);
      await tester.pumpAndSettle();

      return repository.sentAdminEmails.single;
    }

    testWidgets('keeps only the address inside <...> when a mail client pasted a display name', (tester) async {
      expect(await savedFrom(tester, 'Jake Uren <jake.uren@htw.com.au>'), ['jake.uren@htw.com.au']);
    });

    testWidgets('splits a pasted list on commas, semicolons and newlines, whatever each entry looks like', (tester) async {
      expect(
        await savedFrom(
          tester,
          'Jake Uren <jake.uren@htw.com.au>, plain@example.com; <bare@example.com>\n  spaced@example.com  ',
        ),
        ['jake.uren@htw.com.au', 'plain@example.com', 'bare@example.com', 'spaced@example.com'],
      );
    });

    testWidgets('leaves plain addresses unchanged', (tester) async {
      expect(
        await savedFrom(tester, 'a@example.com\nB.Mixed+tag@Example.com'),
        ['a@example.com', 'B.Mixed+tag@Example.com'],
      );
    });
  });

  group('the Administrators hint', () {
    testWidgets('tells a first setup to enable sign-in with an empty list, sign in, then add the list', (tester) async {
      await pumpScreen(
        tester,
        _FakeAuthenticationRepository(stored: _settings(isEnabled: false, adminEmails: const [])),
      );

      // AdminSettingsController.EnsureCallerKeepsAccess refuses any list sent with sign-in on while
      // nobody is signed in, so this is the only path, and the hint must not promise another.
      expect(find.textContaining('First setup takes two saves'), findsOneWidget);
      expect(find.textContaining('Enable sign-in with this list empty, sign in, then come back and add the list'), findsOneWidget);
      expect(find.textContaining('With sign-in required, the list must include your own email'), findsOneWidget);
    });
  });
}
