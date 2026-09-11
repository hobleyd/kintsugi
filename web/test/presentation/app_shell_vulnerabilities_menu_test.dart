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

/// Vulnerabilities is a menu with two screens under it: what is being exploited, and the mapping
/// queue that decides what gets assessed at all.
///
/// `/vulnerabilities` stays CVE Reporting rather than becoming a bare heading, so a bookmark made
/// before the split still lands on the page it was made on — and the parent entry highlights on
/// `startsWith`, so CVE Mapping does not leave the section looking unvisited.
void main() {
  setUp(() {
    SharedPreferences.setMockInitialValues({});
    locator.registerSingleton(GetServerVersion(_FakeServerInfoRepository()));
  });

  tearDown(() => locator.reset());

  Future<void> pumpShell(WidgetTester tester, String location) async {
    tester.view.physicalSize = const Size(1400, 1400);
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

  /// Read off the `Semantics` each nav entry carries, the way the Applications menu's test does.
  /// Asserted on the two sub-entries rather than on the parent's label, because "Vulnerabilities"
  /// now names two things in this sidebar — the top-level entry and the Settings one.
  bool isSelected(WidgetTester tester, String label) => tester
      .widgetList<Semantics>(
        find.byWidgetPredicate((w) => w is Semantics && w.properties.label == label),
      )
      .any((s) => s.properties.selected ?? false);

  testWidgets('lists both Vulnerabilities screens under the menu', (tester) async {
    await pumpShell(tester, Routes.vulnerabilities);

    // Alphabetical by label, so Mapping is offered above Reporting.
    expect(find.text('CVE MAPPING'), findsOneWidget);
    expect(find.text('CVE REPORTING'), findsOneWidget);
  });

  testWidgets('highlights CVE Reporting on the unsuffixed path', (tester) async {
    await pumpShell(tester, Routes.vulnerabilities);

    expect(isSelected(tester, 'CVE Reporting'), isTrue);
    expect(isSelected(tester, 'CVE Mapping'), isFalse);
  });

  /// The parent is `startsWith`, so the section stays lit on the sub-route — and CVE Reporting,
  /// whose path is a prefix of this one, must not light up with it.
  testWidgets('keeps the section highlighted while on CVE Mapping', (tester) async {
    await pumpShell(tester, Routes.vulnerabilitiesMapping);

    expect(isSelected(tester, 'CVE Mapping'), isTrue);
    expect(isSelected(tester, 'CVE Reporting'), isFalse);
    // Safe to read the ambiguous label here: the Settings entry of the same name is only selected
    // on its own path, so on this one the parent is the sole candidate.
    expect(isSelected(tester, 'Vulnerabilities'), isTrue);
  });

  /// `/settings/vulnerabilities` is a different section that happens to share the word. It must
  /// not pick up this menu's highlight.
  testWidgets('is not highlighted from the Settings screen of the same name', (tester) async {
    await pumpShell(tester, Routes.settingsVulnerabilities);

    expect(isSelected(tester, 'CVE Reporting'), isFalse);
    expect(isSelected(tester, 'CVE Mapping'), isFalse);
  });
}
