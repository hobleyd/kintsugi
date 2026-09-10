import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/router/app_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/theme/theme_cubit.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/server_info_usecases.dart';
import 'package:kintsugi_web/presentation/shell/app_shell.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'session_bloc_test.dart' show FakeSessionRepository, blocFor, readySession;

class _FakeServerInfoRepository implements ServerInfoRepository {
  @override
  Future<String> version() => Completer<String>().future;
}

/// The Applications sidebar entry is a menu with two screens under it.
///
/// The parent highlights on `startsWith`, not equality, for the same reason the Hosts entry does:
/// `/applications/failed` is an Applications route, and a sidebar with nothing highlighted on it
/// reads as having navigated out of the app.
void main() {
  setUp(() {
    SharedPreferences.setMockInitialValues({});
    locator.registerSingleton(GetServerVersion(_FakeServerInfoRepository()));
  });

  tearDown(() => locator.reset());

  Future<void> pumpShell(WidgetTester tester, String location) async {
    tester.view.physicalSize = const Size(1400, 1200);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final preferences = await SharedPreferences.getInstance();
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider.value(value: blocFor(FakeSessionRepository(session: readySession()))),
          BlocProvider(create: (_) => ThemeCubit(preferences)),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          home: AppShell(location: location, child: const SizedBox()),
        ),
      ),
    );
    await tester.pump();
  }

  testWidgets('lists both Applications screens under the menu', (tester) async {
    await pumpShell(tester, Routes.applications);

    expect(find.text('APPLICATIONS'), findsOneWidget);
    expect(find.text('CURRENTLY INSTALLED'), findsOneWidget);
    expect(find.text('FAILED UPDATES'), findsOneWidget);
  });

  /// Read off the `Semantics` each nav entry carries — which is the same `selected` the sidebar
  /// derives its own highlight from, and the state it exposes to assistive technology the way
  /// `aria-current="page"` did. Asserting on that rather than on a colour keeps this test about
  /// which entry is current, not about the palette.
  bool isSelected(WidgetTester tester, String label) => tester
      .widgetList<Semantics>(
        find.byWidgetPredicate((w) => w is Semantics && w.properties.label == label),
      )
      .any((s) => s.properties.selected ?? false);

  testWidgets('keeps Applications highlighted while on Failed Updates', (tester) async {
    await pumpShell(tester, Routes.applicationsFailed);

    expect(isSelected(tester, 'Applications'), isTrue);
    expect(isSelected(tester, 'Failed Updates'), isTrue);
    expect(isSelected(tester, 'Currently Installed'), isFalse);
  });

  testWidgets('highlights Currently Installed rather than Failed Updates on the other screen',
      (tester) async {
    await pumpShell(tester, Routes.applications);

    expect(isSelected(tester, 'Applications'), isTrue);
    expect(isSelected(tester, 'Currently Installed'), isTrue);
    expect(isSelected(tester, 'Failed Updates'), isFalse);
  });

  /// The menu must not claim a screen that is not under it — `/applications` is a prefix of nothing
  /// else today, but the check is `startsWith`, so this is what would notice if it were.
  testWidgets('is not highlighted from another section', (tester) async {
    await pumpShell(tester, Routes.hosts);

    expect(isSelected(tester, 'Applications'), isFalse);
    expect(isSelected(tester, 'Hosts'), isTrue);
  });
}
