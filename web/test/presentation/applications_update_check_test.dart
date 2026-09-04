import 'dart:async';

import 'package:bloc_test/bloc_test.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/domain/entities/application.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/application_usecases.dart';
import 'package:kintsugi_web/domain/usecases/upgrade_path_usecases.dart';
import 'package:kintsugi_web/presentation/applications/applications_bloc.dart';

/// The per-row Refresh icon: one script's `--update-version`, run on the server, with the outcome
/// said out loud above the table. What these pin is the part the row's own columns cannot show — a
/// check that succeeded and changed nothing, which without the notice looks like the icon did
/// nothing — and that the overview is re-read afterwards, since the result carries no version.
UpgradePathSummary summary({String latestVersion = '142.0'}) => UpgradePathSummary(
      applicationName: 'Firefox',
      platform: 'macOS',
      status: UpgradePathStatus.found,
      statusKey: 'up-to-date',
      latestVersion: latestVersion,
      method: UpgradeMethod.script,
      downloadUrl: null,
      command: null,
      instructions: null,
      sourceUrl: null,
      notes: null,
      checkedUtc: DateTime.utc(2026, 9, 1),
      hostCount: 1,
      upToDateHostCount: 1,
      updateAvailableHostCount: 0,
      hostNames: const ['alpha'],
      hostNamesNeedingUpdate: const [],
      script: '#!/bin/bash',
      scriptSignature: 'signed',
    );

ApplicationOverview overview({String latestVersion = '142.0'}) => ApplicationOverview(
      applications: [
        ApplicationRow(
          name: 'Firefox',
          hostCount: 1,
          hostNames: const ['alpha'],
          upgradePaths: [summary(latestVersion: latestVersion)],
          children: const [],
        ),
      ],
      totalApplicationCount: 1,
      allHostNames: const ['alpha'],
    );

ApplicationTableRow rowFor(ApplicationOverview overview) => ApplicationTableRow(
      application: overview.applications.single,
      upgradePath: overview.applications.single.upgradePaths.single,
      isChild: false,
    );

class FakeApplicationRepository implements ApplicationRepository {
  FakeApplicationRepository(this.next);

  ApplicationOverview next;
  int reads = 0;

  @override
  Future<ApplicationOverview> overview() async {
    reads++;
    return next;
  }
}

/// Answers `checkUpdate` from a completer so a test can hold the round trip open and look at the
/// state while it is in flight — or, with [failure] set, refuses it outright the way `ApiClient`
/// does when the server cannot be reached. The two status reads are what the screen's run-progress
/// blocs ask on mount; everything else is unreachable from the blocs under test.
///
/// The completer is created on first use rather than in the constructor. A widget test's body runs
/// under `FakeAsync`, and a `Completer` made in `setUp` belongs to the real zone, so completing it
/// schedules its continuation where `tester.pump` never looks and the bloc waits forever.
class FakeUpgradePathRepository implements UpgradePathRepository {
  late final completer = Completer<UpdateCheckResult>();
  final checked = <(String, String)>[];
  ApiException? failure;

  @override
  Future<UpdateCheckResult> checkUpdate({
    required String applicationName,
    required String platform,
  }) async {
    checked.add((applicationName, platform));
    if (failure case final failure?) throw failure;
    return completer.future;
  }

  @override
  Future<UpgradePathScanStatus> scanStatus() async => const UpgradePathScanStatus.idle();

  @override
  Future<UpdateCheckStatus> updateCheckStatus() async => const UpdateCheckStatus.idle();

  @override
  dynamic noSuchMethod(Invocation invocation) => throw UnimplementedError();
}

UpdateCheckResult result({required bool success, bool versionChanged = false, String? note}) =>
    UpdateCheckResult(
      applicationName: 'Firefox',
      platform: 'macOS',
      success: success,
      versionChanged: versionChanged,
      note: note,
    );

void main() {
  late FakeApplicationRepository applications;
  late FakeUpgradePathRepository upgradePaths;

  ApplicationsBloc build() => ApplicationsBloc(
        getOverview: GetApplicationOverview(applications),
        checkUpdate: CheckApplicationUpdate(upgradePaths),
      );

  setUp(() {
    applications = FakeApplicationRepository(overview());
    upgradePaths = FakeUpgradePathRepository();
  });

  blocTest<ApplicationsBloc, ApplicationsState>(
    'marks the row as checking while the round trip is open and sends its name and platform',
    build: build,
    act: (bloc) => bloc.add(ApplicationUpdateCheckRequested(rowFor(overview()))),
    verify: (bloc) {
      expect(bloc.state.checkingRowKeys, {'Firefox macOS'});
      expect(bloc.state.checkNotice, isNull);
      expect(upgradePaths.checked, [('Firefox', 'macOS')]);
    },
  );

  blocTest<ApplicationsBloc, ApplicationsState>(
    'a check that changed nothing still says so, since the row itself cannot',
    build: build,
    act: (bloc) async {
      bloc.add(ApplicationUpdateCheckRequested(rowFor(overview())));
      await Future<void>.delayed(Duration.zero);
      upgradePaths.completer.complete(result(success: true));
    },
    wait: Duration.zero,
    verify: (bloc) {
      expect(bloc.state.checkingRowKeys, isEmpty);
      expect(bloc.state.checkNotice?.success, isTrue);
      expect(bloc.state.checkNotice?.message, contains('unchanged'));
    },
  );

  blocTest<ApplicationsBloc, ApplicationsState>(
    're-reads the overview after a check, because the result carries no version',
    build: build,
    act: (bloc) async {
      applications.next = overview(latestVersion: '143.0');
      bloc.add(ApplicationUpdateCheckRequested(rowFor(overview())));
      await Future<void>.delayed(Duration.zero);
      upgradePaths.completer.complete(result(success: true, versionChanged: true));
    },
    wait: Duration.zero,
    verify: (bloc) {
      expect(applications.reads, 1);
      expect(bloc.state.overview.applications.single.upgradePaths.single.latestVersion, '143.0');
      expect(bloc.state.checkNotice?.message, contains('newer version was found'));
    },
  );

  blocTest<ApplicationsBloc, ApplicationsState>(
    'a script that answered nothing is reported with the server\'s note, as a failure',
    build: build,
    act: (bloc) async {
      bloc.add(ApplicationUpdateCheckRequested(rowFor(overview())));
      await Future<void>.delayed(Duration.zero);
      upgradePaths.completer
          .complete(result(success: false, note: 'The script did not report a version.'));
    },
    wait: Duration.zero,
    verify: (bloc) {
      expect(bloc.state.checkNotice?.success, isFalse);
      expect(
        bloc.state.checkNotice?.message,
        'Firefox on macOS: The script did not report a version.',
      );
    },
  );

  blocTest<ApplicationsBloc, ApplicationsState>(
    'a failed request clears the checking mark rather than leaving the spinner forever',
    build: build,
    act: (bloc) {
      upgradePaths.failure = const ApiException('Kintsugi cannot be reached.');
      bloc.add(ApplicationUpdateCheckRequested(rowFor(overview())));
    },
    wait: Duration.zero,
    verify: (bloc) {
      expect(bloc.state.checkingRowKeys, isEmpty);
      expect(bloc.state.checkNotice?.success, isFalse);
      expect(bloc.state.checkNotice?.message, contains('cannot be reached'));
    },
  );

  blocTest<ApplicationsBloc, ApplicationsState>(
    'pressing the icon again while a check is running does not start a second one',
    build: build,
    act: (bloc) => bloc
      ..add(ApplicationUpdateCheckRequested(rowFor(overview())))
      ..add(ApplicationUpdateCheckRequested(rowFor(overview()))),
    verify: (_) => expect(upgradePaths.checked, hasLength(1)),
  );
}
