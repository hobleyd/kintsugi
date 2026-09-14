import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/diagnostics/diagnostics_log.dart';
import 'package:kintsugi_web/core/router/app_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/theme/theme_cubit.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/application_usecases.dart';
import 'package:kintsugi_web/domain/usecases/server_info_usecases.dart';
import 'package:kintsugi_web/domain/usecases/upgrade_path_usecases.dart';
import 'package:kintsugi_web/presentation/applications/applications_screen.dart';
import 'package:kintsugi_web/presentation/applications/background_run_bloc.dart';
import 'package:kintsugi_web/presentation/applications/run_progress_mappers.dart';
import 'package:kintsugi_web/presentation/applications/widgets/run_progress_view.dart';
import 'package:kintsugi_web/presentation/shell/app_shell.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'applications_update_check_test.dart'
    show FakeApplicationRepository, FakeUpgradePathRepository, overview;
import 'session_bloc_test.dart' show FakeSessionRepository, blocFor, readySession;

class _FakeServerInfoRepository implements ServerInfoRepository {
  @override
  Future<String> version() => Completer<String>().future;
}

/// A concrete run bloc, because [RunProgressView] is generic over the bloc type and a
/// `BlocProvider` has to provide that exact type. The two real ones differ from this only in which
/// two calls they are built with.
class _RunBloc extends BackgroundRunBloc {
  _RunBloc({required super.start, required super.status});
}

UpdateCheckStatus _finished({List<String> notes = const [], String? faultReason}) =>
    UpdateCheckStatus(
      isRunning: false,
      total: 4,
      completed: 4,
      updated: 1,
      unchanged: 1,
      failed: 1,
      skipped: 1,
      startedUtc: DateTime.utc(2026, 9, 14, 1),
      completedUtc: DateTime.utc(2026, 9, 14, 2),
      faultReason: faultReason,
      notes: notes,
    );

/// The diagnostics panel: where a run's error and log output goes, and why it is the shell's
/// rather than the screen's.
///
/// The point being pinned is survival. A run's notes are a line per row it could not check, and
/// what somebody does next with that list is navigate to the screens it names — so the list has to
/// outlive the screen that produced it, which is the one thing an alert above a table cannot do.
void main() {
  setUp(() {
    SharedPreferences.setMockInitialValues({});
    locator.registerSingleton(GetServerVersion(_FakeServerInfoRepository()));
  });

  tearDown(() => locator.reset());

  group('DiagnosticsLog', () {
    test('a run with output opens the panel; a clean success does not', () {
      final log = DiagnosticsLog();

      log.record(title: 'Check for Updates', kind: DiagnosticsKind.success, summary: 'All fine.');
      expect(log.isOpen, isFalse, reason: 'nothing to read, so nothing to slide over the page');
      expect(log.count, 1, reason: 'still recorded — hidden is not the same as dropped');

      log.record(
        title: 'Check for Updates',
        kind: DiagnosticsKind.success,
        summary: '1 updated, 1 failed.',
        lines: const ['Slack: the script did not report a version.'],
      );
      expect(log.isOpen, isTrue);
    });

    test('a failure opens it even with nothing listed', () {
      final log = DiagnosticsLog()
        ..record(
          title: 'Check for Updates',
          kind: DiagnosticsKind.error,
          summary: 'Could not start.',
        );

      expect(log.isOpen, isTrue);
    });

    test('newest first, and bounded so a long-lived tab cannot grow without limit', () {
      final log = DiagnosticsLog();
      for (var i = 0; i <= DiagnosticsLog.retained; i++) {
        log.record(title: 'Run $i', kind: DiagnosticsKind.info);
      }

      expect(log.count, DiagnosticsLog.retained);
      expect(log.entries.first.title, 'Run ${DiagnosticsLog.retained}');
      expect(log.entries.last.title, 'Run 1', reason: 'the oldest was dropped, not the newest');
    });

    test('dismissing the last entry hides the panel rather than leaving it empty', () {
      final log = DiagnosticsLog()
        ..record(title: 'Check for Updates', kind: DiagnosticsKind.error, summary: 'Broke.');

      log.dismiss(log.entries.single.id);

      expect(log.isEmpty, isTrue);
      expect(log.isOpen, isFalse);
    });
  });

  group('the panel in the shell', () {
    late DiagnosticsLog log;

    Future<void> pumpShell(WidgetTester tester, String location) async {
      tester.view.physicalSize = const Size(1600, 1200);
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
            home: AppShell(
              location: location,
              diagnostics: log,
              child: const Center(child: Text('the page')),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    setUp(() => log = DiagnosticsLog());

    testWidgets("shows a run's output, and keeps showing it after navigating", (tester) async {
      await pumpShell(tester, Routes.applications);
      expect(find.text('DIAGNOSTICS'), findsNothing);

      log.record(
        title: 'Check for Updates',
        kind: DiagnosticsKind.success,
        summary: '1 updated, 1 failed.',
        source: 'Installed Applications',
        lines: const ['Slack: the script did not report a version.'],
      );
      await tester.pumpAndSettle();

      expect(find.text('DIAGNOSTICS'), findsOneWidget);
      expect(find.text('Slack: the script did not report a version.'), findsOneWidget);

      // What a navigation looks like to this shell: the same element, a different location. The
      // ShellRoute rebuilds only the page beside it.
      await pumpShell(tester, Routes.upgradeScripts);

      expect(
        find.text('Slack: the script did not report a version.'),
        findsOneWidget,
        reason: 'the output outlives the screen that produced it — the whole point of it',
      );
      expect(find.text('UPGRADE SCRIPTS'), findsOneWidget, reason: 'the menu is still usable');
    });

    testWidgets('hiding it leaves a way back, and the entries survive', (tester) async {
      await pumpShell(tester, Routes.applications);
      log.record(
        title: 'Check for Updates',
        kind: DiagnosticsKind.error,
        summary: 'The run stopped.',
      );
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('Hide this panel'));
      await tester.pumpAndSettle();

      expect(find.text('DIAGNOSTICS'), findsNothing);
      expect(log.count, 1, reason: 'hidden, not cleared');

      await tester.tap(find.text('DIAGNOSTICS (1)'));
      await tester.pumpAndSettle();

      expect(find.text('The run stopped.'), findsOneWidget);
    });

    testWidgets('takes width from the page rather than covering it', (tester) async {
      await pumpShell(tester, Routes.applications);
      final full = tester.getRect(find.text('the page'));

      log.record(title: 'Check for Updates', kind: DiagnosticsKind.error, summary: 'Broke.');
      await tester.pumpAndSettle();

      final panel = tester.getRect(find.text('DIAGNOSTICS'));
      final page = tester.getRect(find.text('the page'));
      final menu = tester.getRect(find.text('KINTSUGI'));

      expect(
        page.center.dx,
        lessThan(full.center.dx),
        reason: 'the page moved left — it reflowed rather than being overlaid',
      );
      expect(panel.left, greaterThan(page.left), reason: 'the panel docks to the right edge');
      expect(
        menu.left,
        lessThan(page.left),
        reason: 'the navigation is at the other end and keeps its place',
      );
    });
  });

  testWidgets('the widest table in the product still lays out beside it', (tester) async {
    // The one thing taking width from the page can break. Applications is eight columns with a
    // 1100pt floor; the sidebar takes 240 and the panel 320, so at 1280 the table is well past
    // what is left and has to scroll horizontally inside its own panel rather than overflow.
    // 1280x800 rather than the 1400x900 the other screen tests pin, because that is the size at
    // which it would go wrong.
    final applications = FakeApplicationRepository(overview());
    final upgradePaths = FakeUpgradePathRepository();
    locator
      ..registerSingleton(GetApplicationOverview(applications))
      ..registerSingleton(RequestForcedPatchRuns(applications))
      ..registerSingleton(CheckApplicationUpdate(upgradePaths))
      ..registerSingleton(StartUpgradePathScan(upgradePaths))
      ..registerSingleton(GetUpgradePathScanStatus(upgradePaths))
      ..registerSingleton(StartUpdateCheck(upgradePaths))
      ..registerSingleton(GetUpdateCheckStatus(upgradePaths));

    tester.view.physicalSize = const Size(1280, 800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final log = DiagnosticsLog()
      ..record(
        title: 'Check for Updates',
        kind: DiagnosticsKind.success,
        summary: '2 updated, 1 failed, 3 with nothing to check.',
        source: 'Installed Applications',
        lines: const ['Slack (macOS): the script did not report a version.'],
      );

    final preferences = await SharedPreferences.getInstance();
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider.value(value: blocFor(FakeSessionRepository(session: readySession()))),
          BlocProvider(create: (_) => ThemeCubit(preferences)),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          home: AppShell(
            location: Routes.applications,
            diagnostics: log,
            child: const ApplicationsScreen(),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull, reason: 'nothing overflowed — the table scrolls');
    expect(find.text('Slack (macOS): the script did not report a version.'), findsOneWidget);

    // Disposes the screen so its polling blocs close before the fake clock is checked for timers.
    await tester.pumpWidget(const SizedBox());
  });

  group('a finished run records into it', () {
    late DiagnosticsLog log;

    Future<void> pumpRun(
      WidgetTester tester, {
      required UpdateCheckStatus status,
      bool collecting = true,
    }) async {
      final view = BlocProvider(
        create: (_) => _RunBloc(
          start: () async => startedProgress(
            RunStarted(started: true, status: status),
            updateCheckProgress,
          ),
          status: () async => updateCheckProgress(status),
        )..add(const RunStartRequested()),
        child: RunProgressView<_RunBloc>(
          title: 'Check for Updates',
          source: 'Installed Applications',
          onFinished: () {},
        ),
      );

      await tester.pumpWidget(
        MaterialApp(
          theme: AppTheme.light(),
          home: Scaffold(
            body: collecting ? DiagnosticsLogScope(log: log, child: view) : view,
          ),
        ),
      );
      await tester.pumpAndSettle();
    }

    setUp(() => log = DiagnosticsLog());

    testWidgets(
      'the notes go to the panel, and the page says so instead of repeating them',
      (tester) async {
        await pumpRun(tester, status: _finished(notes: const ['Slack: no script to check.']));

        expect(log.count, 1);
        final entry = log.entries.single;
        expect(entry.title, 'Check for Updates');
        expect(entry.source, 'Installed Applications');
        expect(entry.lines, const ['Slack: no script to check.']);
        expect(entry.summary, contains('1 updated'));

        expect(find.text('Worth a look:'), findsNothing);
        expect(find.textContaining('1 note(s) worth a look'), findsOneWidget);

        await tester.pumpWidget(const SizedBox());
      },
    );

    testWidgets('a fault is recorded as one, with no notes attached', (tester) async {
      await pumpRun(
        tester,
        status: _finished(notes: const ['Slack: no script to check.'], faultReason: 'The run died.'),
      );

      expect(log.entries.single.kind, DiagnosticsKind.error);
      expect(log.entries.single.summary, 'The run died.');
      expect(
        log.entries.single.lines,
        isEmpty,
        reason: "a faulted run's counts are of work it never finished",
      );

      await tester.pumpWidget(const SizedBox());
    });

    testWidgets(
      'off the shell the notes are still listed on the page, never dropped',
      (tester) async {
        await pumpRun(
          tester,
          collecting: false,
          status: _finished(notes: const ['Slack: no script to check.']),
        );

        expect(find.text('Worth a look:'), findsOneWidget);
        expect(find.text('- Slack: no script to check.'), findsOneWidget);

        await tester.pumpWidget(const SizedBox());
      },
    );
  });
}
