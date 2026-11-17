import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/text_bits.dart';
import 'package:kintsugi_web/domain/entities/session.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/settings_usecases.dart';
import 'package:kintsugi_web/presentation/session/session_bloc.dart';
import 'package:kintsugi_web/presentation/settings/administrators_screen.dart';

import 'session_bloc_test.dart';

class _FakeAdministratorsRepository implements AdministratorsRepository {
  _FakeAdministratorsRepository(this.admins, {this.readFailure, this.addFailure});

  List<Administrator> admins;
  final ApiException? readFailure;
  final ApiException? addFailure;

  final added = <List<String>>[];
  final removed = <String>[];

  @override
  Future<List<Administrator>> list() async {
    if (readFailure != null) throw readFailure!;
    return admins;
  }

  @override
  Future<List<Administrator>> add(List<String> emails) async {
    if (addFailure != null) throw addFailure!;
    added.add(emails);
    admins = [
      ...admins,
      for (final email in emails)
        if (!admins.any((a) => a.email == email.toLowerCase()))
          Administrator(email: email.toLowerCase(), addedBy: 'me@example.com', addedUtc: DateTime.utc(2026, 11, 2, 10)),
    ];
    return admins;
  }

  @override
  Future<List<Administrator>> remove(String email) async {
    removed.add(email);
    admins = admins.where((a) => a.email != email).toList();
    return admins;
  }
}

Administrator _admin(String email, {String? by, DateTime? at}) => Administrator(email: email, addedBy: by, addedUtc: at);

final _jake = _admin('jake@example.com', by: 'me@example.com', at: DateTime.utc(2026, 11, 2, 9, 14));
final _me = _admin('me@example.com');

Session _session({bool signInRequired = true}) => Session(
      authenticationSettingsSaved: true,
      authenticationEnabled: signInRequired,
      signedIn: true,
      authorized: true,
      userName: 'me',
      email: 'me@example.com',
      providerDisplayName: 'Google Workspace',
      canSignIn: true,
      callbackUrl: 'https://kintsugi.example.com/signin-oidc',
      signOutCallbackUrl: 'https://kintsugi.example.com/signout-callback-oidc',
    );

void main() {
  tearDown(() => locator.reset());

  Future<_FakeAdministratorsRepository> pumpScreen(
    WidgetTester tester,
    _FakeAdministratorsRepository repository, {
    bool signInRequired = true,
  }) async {
    locator
      ..registerSingleton(GetAdministrators(repository))
      ..registerSingleton(AddAdministrators(repository))
      ..registerSingleton(RemoveAdministrator(repository));

    tester.view.physicalSize = const Size(1400, 1600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: BlocProvider(
          create: (_) => blocFor(FakeSessionRepository(session: _session(signInRequired: signInRequired)))
            ..add(const SessionRequested()),
          child: const Scaffold(body: AdministratorsScreen()),
        ),
      ),
    );
    await tester.pumpAndSettle();
    return repository;
  }

  Finder deleteFor(String email) => find.byKey(ValueKey('admin-delete-$email'));

  group('the list', () {
    testWidgets('shows only the names while closed', (tester) async {
      await pumpScreen(tester, _FakeAdministratorsRepository([_me, _jake]));

      expect(find.text('me@example.com'), findsOneWidget);
      expect(find.text('jake@example.com'), findsOneWidget);
      expect(find.text('ADDED BY'), findsNothing);
      expect(deleteFor('jake@example.com'), findsNothing);
    });

    testWidgets('marks the signed-in caller as You', (tester) async {
      await pumpScreen(tester, _FakeAdministratorsRepository([_me, _jake]));

      expect(find.text('YOU'), findsOneWidget);
    });

    testWidgets('opens a name on a click to show who added it, when, and a delete button', (tester) async {
      await pumpScreen(tester, _FakeAdministratorsRepository([_me, _jake]));

      await tester.tap(find.text('jake@example.com'));
      await tester.pumpAndSettle();

      expect(find.text('ADDED BY'), findsOneWidget);
      expect(find.text('ADDED ON'), findsOneWidget);
      // The adder is shown as the email that did it, and the date as a timestamp (zone-dependent).
      expect(find.text('me@example.com'), findsNWidgets(2));
      expect(find.byType(LocalTimestamp), findsOneWidget);
      expect(deleteFor('jake@example.com'), findsOneWidget);

      await tester.tap(find.text('jake@example.com'));
      await tester.pumpAndSettle();
      expect(find.text('ADDED BY'), findsNothing);
    });

    testWidgets('gives the caller no delete button, only the reason', (tester) async {
      await pumpScreen(tester, _FakeAdministratorsRepository([_me, _jake]));

      await tester.tap(find.text('me@example.com'));
      await tester.pumpAndSettle();

      expect(deleteFor('me@example.com'), findsNothing);
      expect(find.textContaining("can't delete yourself"), findsOneWidget);
    });

    testWidgets('shows a dash for an entry listed before tracking', (tester) async {
      await pumpScreen(tester, _FakeAdministratorsRepository([_me, _admin('old@example.com')]));

      await tester.tap(find.text('old@example.com'));
      await tester.pumpAndSettle();

      expect(find.text('—'), findsNWidgets(2));
    });
  });

  group('deleting', () {
    testWidgets('asks first, then deletes only once confirmed', (tester) async {
      final repository = await pumpScreen(tester, _FakeAdministratorsRepository([_me, _jake]));

      await tester.tap(find.text('jake@example.com'));
      await tester.pumpAndSettle();
      await tester.tap(deleteFor('jake@example.com'));
      await tester.pumpAndSettle();

      expect(find.text('Delete administrator?'), findsOneWidget);
      expect(repository.removed, isEmpty);

      await tester.tap(find.text('DELETE').last);
      await tester.pumpAndSettle();

      expect(repository.removed, ['jake@example.com']);
      expect(find.text('jake@example.com'), findsNothing);
      expect(find.text('Deleted jake@example.com.'), findsOneWidget);
    });

    testWidgets('deletes nothing when the dialog is cancelled', (tester) async {
      final repository = await pumpScreen(tester, _FakeAdministratorsRepository([_me, _jake]));

      await tester.tap(find.text('jake@example.com'));
      await tester.pumpAndSettle();
      await tester.tap(deleteFor('jake@example.com'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('CANCEL'));
      await tester.pumpAndSettle();

      expect(repository.removed, isEmpty);
      expect(find.text('jake@example.com'), findsOneWidget);
    });

    testWidgets('offers no Delete for the last one, and says why', (tester) async {
      final repository = await pumpScreen(tester, _FakeAdministratorsRepository([_jake]));

      await tester.tap(find.text('jake@example.com'));
      await tester.pumpAndSettle();

      expect(deleteFor('jake@example.com'), findsNothing);
      expect(find.textContaining("The last administrator can't be deleted"), findsOneWidget);
      expect(repository.removed, isEmpty);
    });
  });

  group('adding', () {
    final field = find.byType(TextField);

    testWidgets('sends the address inside a pasted display name', (tester) async {
      final repository = await pumpScreen(tester, _FakeAdministratorsRepository([_me]));

      await tester.enterText(field, 'Jake Uren <jake@example.com>, anna@example.com');
      await tester.tap(find.text('ADD ADMINISTRATOR'));
      await tester.pumpAndSettle();

      expect(repository.added, [
        ['jake@example.com', 'anna@example.com'],
      ]);
      expect(find.text('Added 2 administrators.'), findsOneWidget);
      expect(find.text('anna@example.com'), findsOneWidget);
    });

    testWidgets('refuses an empty box without asking the server', (tester) async {
      final repository = await pumpScreen(tester, _FakeAdministratorsRepository([_me]));

      await tester.enterText(field, '   ');
      await tester.tap(find.text('ADD ADMINISTRATOR'));
      await tester.pumpAndSettle();

      expect(find.text('Enter an email address.'), findsOneWidget);
      expect(repository.added, isEmpty);
    });

    testWidgets("shows the server's reason, such as the lockout guard", (tester) async {
      await pumpScreen(
        tester,
        _FakeAdministratorsRepository(
          [],
          addFailure: const ApiException(
            'The administrators list must include your own email, or you would lock yourself out.',
          ),
        ),
      );

      await tester.enterText(field, 'jake@example.com');
      await tester.tap(find.text('ADD ADMINISTRATOR'));
      await tester.pumpAndSettle();

      expect(find.textContaining('must include your own email'), findsOneWidget);
    });
  });

  group('banners', () {
    testWidgets('says the list is not enforced while sign-in is off', (tester) async {
      await pumpScreen(tester, _FakeAdministratorsRepository([_me]), signInRequired: false);

      expect(find.textContaining('this list is not enforced yet'), findsOneWidget);
    });

    testWidgets('warns that an empty list admits every account, once sign-in is required', (tester) async {
      await pumpScreen(tester, _FakeAdministratorsRepository([]));

      expect(find.textContaining('every account that can sign in is an administrator'), findsOneWidget);
    });

    testWidgets('does not claim the list is empty when it could not be loaded', (tester) async {
      await pumpScreen(
        tester,
        _FakeAdministratorsRepository([], readFailure: const ApiException('Could not reach the server.')),
      );

      expect(find.text('Could not reach the server.'), findsOneWidget);
      expect(find.textContaining('every account that can sign in is an administrator'), findsNothing);
      expect(find.text('The list could not be loaded.'), findsOneWidget);
    });
  });
}
