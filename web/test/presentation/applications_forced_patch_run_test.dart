import 'package:bloc_test/bloc_test.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/alert_box.dart';
import 'package:kintsugi_web/domain/entities/application.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/domain/usecases/application_usecases.dart';
import 'package:kintsugi_web/domain/usecases/upgrade_path_usecases.dart';
import 'package:kintsugi_web/presentation/applications/applications_bloc.dart';
import 'package:kintsugi_web/presentation/applications/applications_screen.dart';

import 'applications_update_check_test.dart'
    show FakeApplicationRepository, FakeUpgradePathRepository;

/// "Patch now": the Applications screen's emergency action.
///
/// What these pin is the part that is not visible from the icon — *which hosts* it would reach.
/// The set is decided by filters set elsewhere on the page and by which hosts are actually behind,
/// so getting it wrong means instructing machines nobody asked about, and there is nothing on
/// screen afterwards that would show it: no column moves when an instruction is raised.
UpgradePathSummary summary({
  List<String> hostNames = const ['alpha', 'beta'],
  List<String> hostNamesNeedingUpdate = const ['alpha', 'beta'],
  String platform = 'macOS',
  String? scriptSignature = 'signed',
}) =>
    UpgradePathSummary(
      applicationName: 'Firefox',
      platform: platform,
      status: UpgradePathStatus.found,
      statusKey: 'update-available',
      latestVersion: '143.0',
      method: UpgradeMethod.script,
      downloadUrl: null,
      command: null,
      instructions: null,
      sourceUrl: null,
      notes: null,
      checkedUtc: DateTime.utc(2026, 9, 1),
      hostCount: hostNames.length,
      upToDateHostCount: hostNames.length - hostNamesNeedingUpdate.length,
      updateAvailableHostCount: hostNamesNeedingUpdate.length,
      hostNames: hostNames,
      hostNamesNeedingUpdate: hostNamesNeedingUpdate,
      script: '#!/bin/bash',
      scriptSignature: scriptSignature,
    );

ApplicationOverview overviewOf(UpgradePathSummary path) => ApplicationOverview(
      applications: [
        ApplicationRow(
          name: 'Firefox',
          hostCount: path.hostNames.length,
          hostNames: path.hostNames,
          upgradePaths: [path],
          children: const [],
        ),
      ],
      totalApplicationCount: 1,
      allHostNames: path.hostNames,
    );

ApplicationTableRow rowOf(ApplicationOverview overview) => ApplicationTableRow(
      application: overview.applications.single,
      upgradePath: overview.applications.single.upgradePaths.single,
    );

void main() {
  late FakeApplicationRepository applications;
  late FakeUpgradePathRepository upgradePaths;

  ApplicationsBloc build() => ApplicationsBloc(
        getOverview: GetApplicationOverview(applications),
        checkUpdate: CheckApplicationUpdate(upgradePaths),
        forcePatchRuns: RequestForcedPatchRuns(applications),
      );

  setUp(() {
    applications = FakeApplicationRepository(overviewOf(summary()));
    upgradePaths = FakeUpgradePathRepository();
  });

  group('which hosts it reaches', () {
    test('only the ones actually behind on this row', () {
      final overview = overviewOf(summary(hostNamesNeedingUpdate: const ['beta']));
      final state = ApplicationsState(overview: overview);

      // An agent will not patch a row its own work list reports as up to date, so an instruction
      // to `alpha` is a row in the database that can never do anything.
      expect(state.forcedRunHostNamesFor(rowOf(overview)), ['beta']);
    });

    test('narrowed to the host the table is filtered to', () {
      final overview = overviewOf(summary());
      final state = ApplicationsState(
        overview: overview,
        filters: const ApplicationFilters(hostName: 'BETA'),
      );

      expect(state.forcedRunHostNamesFor(rowOf(overview)), ['beta']);
    });

    /// The row's own host list, never the application's: an application installed from Homebrew on
    /// a Mac and from winget on a PC is two rows sharing one application-level list, so reading
    /// that would have the Homebrew row instruct the Windows hosts too.
    test('the row\'s hosts rather than the application\'s', () {
      final path = summary(hostNames: const ['mac-1'], hostNamesNeedingUpdate: const ['mac-1']);
      final overview = ApplicationOverview(
        applications: [
          ApplicationRow(
            name: 'Firefox',
            hostCount: 2,
            hostNames: const ['mac-1', 'pc-1'],
            upgradePaths: [path],
            children: const [],
          ),
        ],
        totalApplicationCount: 1,
        allHostNames: const ['mac-1', 'pc-1'],
      );

      expect(ApplicationsState(overview: overview).forcedRunHostNamesFor(rowOf(overview)), ['mac-1']);
    });

    /// A server older than `hostNamesNeedingUpdate` sends it empty, and an empty target set would
    /// make the action permanently unavailable rather than merely imprecise.
    test('falls back to the row\'s whole host list when the field is absent', () {
      final overview = overviewOf(summary(hostNamesNeedingUpdate: const []));

      expect(
        ApplicationsState(overview: overview).forcedRunHostNamesFor(rowOf(overview)),
        ['alpha', 'beta'],
      );
    });
  });

  group('the request', () {
    blocTest<ApplicationsBloc, ApplicationsState>(
      'sends the application, the platform and the hosts the filters leave',
      build: build,
      seed: () => ApplicationsState(
        overview: overviewOf(summary()),
        filters: const ApplicationFilters(hostName: 'alpha'),
        loading: false,
      ),
      act: (bloc) => bloc.add(ApplicationForcedPatchRunRequested(rowOf(overviewOf(summary())))),
      wait: Duration.zero,
      verify: (bloc) {
        // Field by field: the record holds a List, which compares by identity rather than by value.
        final sent = applications.forced.single;
        expect(sent.applicationName, 'Firefox');
        expect(sent.platform, 'macOS');
        expect(sent.hostNames, ['alpha']);
      },
    );

    /// Nothing on the screen moves when an instruction is raised, so the notice is the only thing
    /// that says the press did anything — and it has to promise an instruction rather than an
    /// outcome, because no host has patched anything yet and none will for at least five minutes.
    blocTest<ApplicationsBloc, ApplicationsState>(
      'says how many hosts were told, and does not claim patching has started',
      build: build,
      seed: () => ApplicationsState(overview: overviewOf(summary()), loading: false),
      act: (bloc) {
        applications.forcedResult = const ForcedPatchRunResult(requested: 2, notRequested: []);
        bloc.add(ApplicationForcedPatchRunRequested(rowOf(overviewOf(summary()))));
      },
      wait: Duration.zero,
      verify: (bloc) {
        final notice = bloc.state.forceNotice!;
        expect(notice.success, isTrue);
        expect(notice.message, contains('2 host(s) will run this upgrade'));
        expect(notice.message, contains('cannot be delayed'));
        expect(bloc.state.forcingRowKeys, isEmpty);
      },
    );

    /// The half an operator acting on an emergency has to see: a filter resolved in the browser can
    /// name a host the server has since removed, and a bare count would hide it.
    blocTest<ApplicationsBloc, ApplicationsState>(
      'names the hosts the server could not reach, and does not call that a success',
      build: build,
      seed: () => ApplicationsState(overview: overviewOf(summary()), loading: false),
      act: (bloc) {
        applications.forcedResult = const ForcedPatchRunResult(requested: 1, notRequested: ['beta']);
        bloc.add(ApplicationForcedPatchRunRequested(rowOf(overviewOf(summary()))));
      },
      wait: Duration.zero,
      verify: (bloc) {
        expect(bloc.state.forceNotice!.success, isFalse);
        expect(bloc.state.forceNotice!.message, contains('Not sent to beta'));
      },
    );

    blocTest<ApplicationsBloc, ApplicationsState>(
      'reports a refused request rather than leaving the row spinning',
      build: build,
      seed: () => ApplicationsState(overview: overviewOf(summary()), loading: false),
      act: (bloc) {
        applications.forceFailure = const ApiException('Not signed in.');
        bloc.add(ApplicationForcedPatchRunRequested(rowOf(overviewOf(summary()))));
      },
      wait: Duration.zero,
      verify: (bloc) {
        expect(bloc.state.forceNotice!.success, isFalse);
        expect(bloc.state.forceNotice!.message, contains('Not signed in.'));
        expect(bloc.state.forcingRowKeys, isEmpty);
      },
    );

    /// The screen only offers the action when there is something to force, so this is the race: a
    /// poll between the icon appearing and the press landing takes the last outdated host off the
    /// row. Sending it would be a 400 from the validator, whose message describes something else.
    blocTest<ApplicationsBloc, ApplicationsState>(
      'sends nothing at all when the filters leave no host behind on this row',
      build: build,
      seed: () => ApplicationsState(
        overview: overviewOf(summary(hostNamesNeedingUpdate: const ['beta'])),
        filters: const ApplicationFilters(hostName: 'alpha'),
        loading: false,
      ),
      act: (bloc) => bloc.add(
        ApplicationForcedPatchRunRequested(rowOf(overviewOf(summary(hostNamesNeedingUpdate: const ['beta'])))),
      ),
      wait: Duration.zero,
      verify: (bloc) {
        expect(applications.forced, isEmpty);
        expect(bloc.state.forceNotice!.success, isFalse);
      },
    );
  });

  group('the button', () {
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

    setUp(() {
      locator
        ..registerSingleton(GetApplicationOverview(applications))
        ..registerSingleton(RequestForcedPatchRuns(applications))
        ..registerSingleton(CheckApplicationUpdate(upgradePaths))
        ..registerSingleton(StartUpgradePathScan(upgradePaths))
        ..registerSingleton(GetUpgradePathScanStatus(upgradePaths))
        ..registerSingleton(StartUpdateCheck(upgradePaths))
        ..registerSingleton(GetUpdateCheckStatus(upgradePaths));
    });

    tearDown(() => locator.reset());

    testWidgets('sits to the left of View script, and names its targets', (tester) async {
      await pumpScreen(tester);

      final force = find.byTooltip('Patch now on 2 host(s)');
      final viewScript = find.byTooltip('View script');
      expect(force, findsOneWidget);

      final forceRect = tester.getRect(force);
      final viewRect = tester.getRect(viewScript);
      expect(forceRect.center.dy, moreOrLessEquals(viewRect.center.dy, epsilon: 1));
      expect(viewRect.center.dx - forceRect.center.dx, moreOrLessEquals(40, epsilon: 1));

      await tester.pumpWidget(const SizedBox());
    });

    /// An enabled button that quietly instructs hosts to do nothing would be worse than one that
    /// says why it cannot: no agent runs an unsigned script, so forcing one is a no-op everywhere.
    testWidgets('is disabled, and says why, when the script is not signed', (tester) async {
      applications.next = overviewOf(summary(scriptSignature: null));
      await pumpScreen(tester);

      expect(
        find.byTooltip('This script is not signed yet, so no agent will run it.'),
        findsOneWidget,
      );

      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('confirms before it sends, and the dialog says the warning cannot be delayed',
        (tester) async {
      await pumpScreen(tester);

      await tester.tap(find.byTooltip('Patch now on 2 host(s)'));
      await tester.pumpAndSettle();

      expect(find.text('Patch Firefox now on 2 host(s)?'), findsOneWidget);
      expect(find.textContaining('cannot delay it'), findsOneWidget);
      expect(applications.forced, isEmpty, reason: 'nothing is sent until the dialog is answered');

      await tester.tap(find.text('CANCEL'));
      await tester.pumpAndSettle();
      expect(applications.forced, isEmpty);

      await tester.tap(find.byTooltip('Patch now on 2 host(s)'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('PATCH NOW'));
      await tester.pumpAndSettle();

      expect(applications.forced.single.hostNames, ['alpha', 'beta']);
      expect(
        find.descendant(
          of: find.byType(AlertBox),
          matching: find.textContaining('will run this upgrade at their next check'),
        ),
        findsOneWidget,
      );

      await tester.pumpWidget(const SizedBox());
    });
  });
}
