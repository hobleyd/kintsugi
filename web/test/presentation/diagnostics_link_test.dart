import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/diagnostics/diagnostics_log.dart';
import 'package:kintsugi_web/core/platform/full_screen.dart';
import 'package:kintsugi_web/core/router/app_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/theme/theme_cubit.dart';
import 'package:kintsugi_web/domain/entities/host.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/application_usecases.dart';
import 'package:kintsugi_web/domain/usecases/host_usecases.dart';
import 'package:kintsugi_web/domain/usecases/server_info_usecases.dart';
import 'package:kintsugi_web/domain/usecases/upgrade_path_usecases.dart';
import 'package:kintsugi_web/presentation/session/session_bloc.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'applications_update_check_test.dart'
    show FakeApplicationRepository, FakeUpgradePathRepository, overview;
import 'session_bloc_test.dart' show FakeSessionRepository, blocFor, readySession;

class _FakeServerInfoRepository implements ServerInfoRepository {
  @override
  Future<String> version() => Completer<String>().future;
}

class _FakeHostRepository implements HostRepository {
  @override
  Future<List<HostSummary>> list() async => const [];

  @override
  Future<void> requestRemoval(String id) async {}
}

class _NeverFullScreen implements FullScreenController {
  @override
  bool get isFullScreen => false;

  @override
  Future<bool> enter() async => false;

  @override
  Future<void> exit() async {}

  @override
  Stream<bool> get onChanged => const Stream.empty();
}

/// Tapping a note in the diagnostics panel filters Installed Applications to the row it names.
///
/// Pumped through the **real** `createRouter`, and that is the whole point of this file rather
/// than a convenience. The navigation this feature performs is `/applications` →
/// `/applications?search=Slack`, which go_router resolves to the *same* `GoRoute` — so the screen's
/// element is reused, its `BlocProvider.create` never runs a second time, and the filter is read
/// from a query string nobody looks at again. `app_router.dart` keys the screen on
/// `state.uri.query` to force a new element. A test that navigated from Hosts instead would pass
/// with that key deleted, which would make it worth nothing.
void main() {
  late DiagnosticsLog log;
  late FakeApplicationRepository applications;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    log = DiagnosticsLog();
    applications = FakeApplicationRepository(overview());
    final upgradePaths = FakeUpgradePathRepository();

    locator
      ..registerSingleton<DiagnosticsLog>(log)
      ..registerSingleton<FullScreenController>(_NeverFullScreen())
      ..registerSingleton(GetServerVersion(_FakeServerInfoRepository()))
      ..registerSingleton(GetHosts(_FakeHostRepository()))
      ..registerSingleton(RequestHostRemoval(_FakeHostRepository()))
      ..registerSingleton(GetApplicationOverview(applications))
      ..registerSingleton(CheckApplicationUpdate(upgradePaths))
      ..registerSingleton(RequestForcedPatchRuns(applications))
      ..registerSingleton(StartUpgradePathScan(upgradePaths))
      ..registerSingleton(GetUpgradePathScanStatus(upgradePaths))
      ..registerSingleton(StartUpdateCheck(upgradePaths))
      ..registerSingleton(GetUpdateCheckStatus(upgradePaths));
  });

  tearDown(() => locator.reset());

  Future<GoRouter> pumpApp(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final session = blocFor(FakeSessionRepository(session: readySession()))
      ..add(const SessionRequested());
    final router = createRouter(session);
    addTearDown(router.dispose);
    final preferences = await SharedPreferences.getInstance();

    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider.value(value: session),
          BlocProvider(create: (_) => ThemeCubit(preferences)),
        ],
        child: MaterialApp.router(theme: AppTheme.light(), routerConfig: router),
      ),
    );
    // `pump`, never `pumpAndSettle`: the splash spins forever by design and the Hosts screen
    // polls, so nothing in this app ever reaches a quiet frame. Two pumps clear the session
    // bootstrap and the redirect that follows it.
    await tester.pump();
    await tester.pump();
    return router;
  }

  testWidgets('a note links from the panel to that application, on the Applications screen',
      (tester) async {
    final router = await pumpApp(tester);
    router.go(Routes.applications);
    await tester.pump();
    await tester.pump();

    expect(applications.reads, 1);

    log.record(
      title: 'Check for Updates',
      kind: DiagnosticsKind.success,
      summary: '0 updated, 1 failed.',
      source: 'Installed Applications',
      lines: const [
        DiagnosticsLine(
          'Firefox (macOS): the script did not report a version.',
          target: '/applications?search=Firefox',
        ),
      ],
    );
    // Long enough for the panel's 180ms slide, so the line is at its final position when tapped.
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));

    await tester.tap(find.text('Firefox (macOS): the script did not report a version.'));
    await tester.pump();
    await tester.pump();

    expect(router.state.uri.toString(), '/applications?search=Firefox');
    expect(
      applications.reads,
      2,
      reason: 'the screen was rebuilt, which is the only way the filter is read again',
    );
    // The search box carries the name, so the table is visibly filtered rather than mysteriously
    // short — `SearchField` seeds itself from the filter for exactly this case.
    expect(find.widgetWithText(TextField, 'Firefox'), findsOneWidget);
    expect(
      find.text('Firefox (macOS): the script did not report a version.'),
      findsOneWidget,
      reason: 'the panel stayed open across the navigation, so note and row are on screen together',
    );

    // Disposes every screen so its polling blocs close before the fake clock is checked.
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a note with no route is not offered as a link', (tester) async {
    final router = await pumpApp(tester);
    router.go(Routes.applications);
    await tester.pump();
    await tester.pump();

    log.record(
      title: 'Check for Updates',
      kind: DiagnosticsKind.success,
      summary: '0 updated, 1 failed.',
      // What the server appends when it bounds the list. It names no row, so there is nowhere for
      // it to lead — see `noteLine`.
      lines: const [DiagnosticsLine('...and 12 more not listed.')],
    );
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));

    await tester.tap(find.text('...and 12 more not listed.'));
    await tester.pump();
    await tester.pump();

    expect(router.state.uri.toString(), Routes.applications, reason: 'a plain line goes nowhere');

    await tester.pumpWidget(const SizedBox());
  });
}
