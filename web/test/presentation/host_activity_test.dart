import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/host_activity_mapper.dart';
import 'package:kintsugi_web/domain/entities/application.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/host_activity.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/application_usecases.dart';
import 'package:kintsugi_web/domain/usecases/host_activity_usecases.dart';
import 'package:kintsugi_web/presentation/host_activity/host_activity_screen.dart';

Map<String, dynamic> _json({
  String os = 'Windows 11 Pro 26H2 (26300)',
  List<Map<String, dynamic>>? events,
  List<Map<String, dynamic>>? pending,
  List<Map<String, dynamic>>? failures,
  int retention = 6,
  String? restartSince,
}) =>
    {
      'hostId': 'h1',
      'hostname': 'BNN-ZO2',
      'operatingSystem': os,
      'agentVersion': '0.12.5',
      'status': 'Online',
      'lastSeenUtc': '2026-11-05T01:00:00Z',
      'retentionMonths': retention,
      if (restartSince != null) ...{
        'restartRequiredSinceUtc': restartSince,
        'restartRequiredBecause': 'linux-image-6.8.0-52-generic',
      },
      'events': events ??
          [
            {
              'id': 'e3',
              'occurredUtc': '2026-11-05T02:00:00Z',
              'kind': 'PatchFailed',
              'subject': 'Windows',
              'detail': 'ERROR 0x80070643 installing KB5044321',
              'count': 2,
            },
            {
              'id': 'e2',
              'occurredUtc': '2026-11-04T09:05:00Z',
              'kind': 'PatchNowRequested',
              'subject': 'Windows',
              'actor': 'david@example.com',
              'count': 1,
            },
            {
              'id': 'e1',
              'occurredUtc': '2026-11-03T10:00:00Z',
              'kind': 'ApplicationPatched',
              'subject': 'Firefox',
              'fromVersion': '131.0',
              'toVersion': '132.0',
              'count': 1,
            },
          ],
      'pendingUpdates': pending ??
          [
            {'name': 'Windows', 'kind': 'OperatingSystem', 'currentVersion': 'Windows 11 Pro', 'availableVersion': '26300.1742', 'failed': true},
            {'name': 'Firefox', 'kind': 'Application', 'currentVersion': '131.0', 'failed': false},
          ],
      'openFailures': failures ??
          [
            {
              'id': 'f1',
              'hostId': 'h1',
              'hostname': 'BNN-ZO2',
              'applicationName': 'Windows',
              'details': 'ERROR 0x80070643',
              'firstFailedUtc': '2026-11-04T02:00:00Z',
              'lastFailedUtc': '2026-11-05T02:00:00Z',
              'failureCount': 2,
              'resolution': 'Outstanding',
              'isOperatingSystemUpdate': true,
              'causeTitle': 'Windows Update error 0x80070643',
              'causeExplanation': 'The update service could not finish installing.',
              'suggestedFix': 'Restart the Windows Update service.',
            },
          ],
      'patchedLast30Days': 1,
      'lastPatchedUtc': '2026-11-03T10:00:00Z',
    };

class _Activity implements HostActivityRepository {
  _Activity(this.json);

  Map<String, dynamic> json;
  final retentionSet = <int>[];

  @override
  Future<HostActivity> get(String hostId) async => hostActivityFromJson(json);

  @override
  Future<int> setRetention(int months) async {
    retentionSet.add(months);
    json = {...json, 'retentionMonths': months};
    return months;
  }
}

class _Applications implements ApplicationRepository {
  final sent = <List<String>>[];

  @override
  Future<ForcedPatchRunResult> forcePatchRuns({
    required String applicationName,
    required String platform,
    required List<String> hostNames,
  }) async {
    sent.add(hostNames);
    return ForcedPatchRunResult(requested: hostNames.length, notRequested: const []);
  }

  @override
  noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  group('the mapper', () {
    test('reads events, pending updates and open failures', () {
      final data = hostActivityFromJson(_json());

      expect(data.hostname, 'BNN-ZO2');
      expect(data.status, HostStatus.online);
      expect(data.events.map((e) => e.kind), [
        HostEventKind.patchFailed,
        HostEventKind.patchNowRequested,
        HostEventKind.applicationPatched,
      ]);
      expect(data.events[1].actor, 'david@example.com');
      expect(data.pendingUpdates.first.isOperatingSystem, isTrue);
      expect(data.openFailures.single.causeTitle, 'Windows Update error 0x80070643');
      expect(data.failedTries, 2);
    });

    test('says what happened in words, with the count and the versions', () {
      final data = hostActivityFromJson(_json());

      expect(data.events[0].summary, 'Failed to install Windows (2 tries)');
      expect(data.events[1].summary, 'Patch now requested for Windows');
      expect(data.events[2].summary, 'Patched Firefox 131.0 → 132.0');
    });

    test('an unknown kind degrades rather than throwing', () {
      expect(hostEventKindFromJson('SomethingNew'), HostEventKind.patchFailed);
    });
  });

  group('the screen', () {
    late _Activity activity;
    late _Applications applications;

    setUp(() {
      activity = _Activity(_json());
      applications = _Applications();
    });
    tearDown(() => locator.reset());

    Future<void> pump(WidgetTester tester) async {
      locator
        ..registerSingleton(GetHostActivity(activity))
        ..registerSingleton(SetHostActivityRetention(activity))
        ..registerSingleton(RequestForcedPatchRuns(applications));

      tester.view.physicalSize = const Size(1500, 1400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: HostActivityScreen(hostId: 'h1'))),
      );
      await tester.pumpAndSettle();
    }

    testWidgets('the overview says why it is failing and counts what is open', (tester) async {
      await pump(tester);

      expect(find.text('ACTIVITY ON BNN-ZO2'), findsOneWidget);
      expect(find.text('WINDOWS UPDATE ERROR 0X80070643'), findsOneWidget);
      expect(find.textContaining('could not finish installing'), findsOneWidget);
      expect(find.text('Suggested fix: Restart the Windows Update service.'), findsOneWidget);
    });

    testWidgets('the Activity tab lists the events, and a Patch now line names who pressed it', (tester) async {
      await pump(tester);

      await tester.tap(find.text('ACTIVITY'));
      await tester.pumpAndSettle();

      expect(find.text('Patch now requested for Windows'), findsOneWidget);
      expect(find.text('by david@example.com'), findsOneWidget);
      expect(find.text('Patched Firefox 131.0 → 132.0'), findsOneWidget);
      expect(find.text('Kept for 6 months.'), findsOneWidget);
    });

    testWidgets('a filter leaves only its kind', (tester) async {
      await pump(tester);
      await tester.tap(find.text('ACTIVITY'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('FAILURES'));
      await tester.pumpAndSettle();

      expect(find.text('Failed to install Windows (2 tries)'), findsOneWidget);
      expect(find.text('Patch now requested for Windows'), findsNothing);
    });

    testWidgets('a failure line opens to the install output', (tester) async {
      await pump(tester);
      await tester.tap(find.text('ACTIVITY'));
      await tester.pumpAndSettle();
      expect(find.textContaining('KB5044321'), findsNothing);

      await tester.tap(find.text('Failed to install Windows (2 tries)'));
      await tester.pumpAndSettle();

      expect(find.textContaining('KB5044321'), findsOneWidget);
    });

    testWidgets('Pending updates marks the failed one', (tester) async {
      await pump(tester);

      await tester.tap(find.text('PENDING UPDATES'));
      await tester.pumpAndSettle();

      expect(find.text('FAILED'), findsOneWidget);
      expect(find.text('WAITING'), findsOneWidget);
    });

    testWidgets('Logs shows the output of the failed try', (tester) async {
      await pump(tester);

      await tester.tap(find.text('LOGS'));
      await tester.pumpAndSettle();

      expect(find.textContaining('KB5044321'), findsOneWidget);
      expect(find.text('COPY'), findsOneWidget);
    });

    testWidgets('Patch now asks to confirm and then sends one instruction for this host', (tester) async {
      await pump(tester);

      await tester.tap(find.text('PATCH NOW'));
      await tester.pumpAndSettle();
      expect(applications.sent, isEmpty, reason: 'nothing is sent until it is confirmed');

      await tester.tap(find.text('PATCH NOW').last);
      await tester.pumpAndSettle();

      expect(applications.sent, [
        ['BNN-ZO2'],
      ]);
      expect(find.textContaining('Patch now sent'), findsOneWidget);
    });

    testWidgets('a host that needs a restart says so, with when and why', (tester) async {
      activity = _Activity(_json(os: 'Ubuntu 24.04', pending: [], failures: [], restartSince: '2026-11-05T10:00:00Z'));
      await pump(tester);

      expect(find.text('RESTART NEEDED'), findsOneWidget);
      expect(find.textContaining('linux-image-6.8.0-52-generic'), findsOneWidget);
    });

    testWidgets('a host that needs no restart shows no restart card', (tester) async {
      await pump(tester);

      expect(find.text('RESTART NEEDED'), findsNothing);
    });

    testWidgets('a host that is not Windows has no Patch now', (tester) async {
      activity = _Activity(_json(os: 'macOS 15.2'));
      await pump(tester);

      expect(find.text('PATCH NOW'), findsNothing);
    });

    testWidgets('a Windows host with nothing waiting or failed has no Patch now', (tester) async {
      activity = _Activity(_json(pending: [], failures: []));
      await pump(tester);

      expect(find.text('PATCH NOW'), findsNothing);
      expect(find.text('NO OPEN FAILURES'), findsOneWidget);
    });

    testWidgets('changing the retention sends the chosen months and shows them', (tester) async {
      await pump(tester);
      await tester.tap(find.text('ACTIVITY'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Change'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('6 months').last);
      await tester.pumpAndSettle();
      await tester.tap(find.text('12 months').last);
      await tester.pumpAndSettle();
      await tester.tap(find.text('SAVE'));
      await tester.pumpAndSettle();

      expect(activity.retentionSet, [12]);
      expect(find.text('Kept for 12 months.'), findsOneWidget);
    });
  });
}
