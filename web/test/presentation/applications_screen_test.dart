import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/alert_box.dart';
import 'package:kintsugi_web/domain/usecases/application_usecases.dart';
import 'package:kintsugi_web/domain/usecases/upgrade_path_usecases.dart';
import 'package:kintsugi_web/presentation/applications/applications_screen.dart';

import 'applications_update_check_test.dart'
    show FakeApplicationRepository, FakeUpgradePathRepository, overview, result;

/// The Upgrade column of a script row: a View-script icon and a Refresh icon side by side.
///
/// Pumps the real [ApplicationsScreen] against the bloc test's fakes, registered in [locator] the
/// way `injection.dart` registers the real ones. What this pins is the surface rather than the
/// bloc — that both icons are there, that they share one cell, and that the Refresh one is
/// replaced by a spinner for exactly as long as the round trip is open and then by a notice.
void main() {
  late FakeApplicationRepository applications;
  late FakeUpgradePathRepository upgradePaths;

  setUp(() {
    applications = FakeApplicationRepository(overview());
    upgradePaths = FakeUpgradePathRepository();
    locator
      ..registerSingleton(GetApplicationOverview(applications))
      ..registerSingleton(CheckApplicationUpdate(upgradePaths))
      ..registerSingleton(StartUpgradePathScan(upgradePaths))
      ..registerSingleton(GetUpgradePathScanStatus(upgradePaths))
      ..registerSingleton(StartUpdateCheck(upgradePaths))
      ..registerSingleton(GetUpdateCheckStatus(upgradePaths));
  });

  tearDown(() => locator.reset());

  final viewScript = find.byTooltip('View script');
  final refresh = find.byTooltip('Check for a new version');
  final checking = find.byTooltip('Checking for a new version');

  Future<void> pumpScreen(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: const Scaffold(body: ApplicationsScreen()),
      ),
    );
    await tester.pumpAndSettle();
  }

  /// Disposes the screen so its polling blocs close before the test's fake clock is checked for
  /// timers still pending.
  Future<void> tearDownScreen(WidgetTester tester) => tester.pumpWidget(const SizedBox());

  testWidgets('a script row shows View script and Refresh as icons in one cell', (tester) async {
    await pumpScreen(tester);

    expect(viewScript, findsOneWidget);
    expect(refresh, findsOneWidget);
    expect(find.text('View script'), findsNothing);

    // Their 48px tap targets overlap; what is asserted is that the two buttons sit flush in one
    // row — a centre-to-centre spacing of one Material 3 icon button (40px) on one line.
    final view = tester.getRect(viewScript);
    final check = tester.getRect(refresh);
    expect(check.center.dy, moreOrLessEquals(view.center.dy, epsilon: 1));
    expect(check.center.dx - view.center.dx, moreOrLessEquals(40, epsilon: 1));

    await tearDownScreen(tester);
  });

  testWidgets('Refresh runs the row\'s check and shows a spinner until it answers', (tester) async {
    await pumpScreen(tester);

    await tester.tap(refresh);
    await tester.pump();

    expect(upgradePaths.checked, [('Firefox', 'macOS')]);
    expect(refresh, findsNothing);
    expect(checking, findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    expect(viewScript, findsOneWidget);
    // The spinner takes the icon's place inside the same button, so nothing in the row moves.
    expect(
      tester.getRect(checking).center.dx - tester.getRect(viewScript).center.dx,
      moreOrLessEquals(40, epsilon: 1),
    );

    applications.next = overview(latestVersion: '143.0');
    upgradePaths.completer.complete(result(success: true, versionChanged: true));
    await tester.pumpAndSettle();

    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(refresh, findsOneWidget);
    expect(find.text('143.0'), findsOneWidget);
    expect(
      find.descendant(
        of: find.byType(AlertBox),
        matching: find.text('Firefox on macOS: a newer version was found.'),
      ),
      findsOneWidget,
    );

    await tearDownScreen(tester);
  });
}
