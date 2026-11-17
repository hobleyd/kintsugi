import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/patch_failure_mapper.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/host.dart';
import 'package:kintsugi_web/domain/entities/patch_failure.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/entities/application.dart';
import 'package:kintsugi_web/domain/usecases/application_usecases.dart';
import 'package:kintsugi_web/domain/usecases/host_usecases.dart';
import 'package:kintsugi_web/domain/usecases/patch_failure_usecases.dart';
import 'package:kintsugi_web/presentation/patch_health/patch_health_model.dart';
import 'package:kintsugi_web/presentation/patch_health/patch_health_screen.dart';

HostSummary _host(
  String id,
  String name, {
  HostStatus status = HostStatus.online,
  bool? osUpdate,
  int appUpdates = 0,
}) =>
    HostSummary(
      id: id,
      hostname: name,
      serialNumber: 'SN-$id',
      operatingSystem: 'Windows 11',
      ipAddress: null,
      status: status,
      lastSeenUtc: DateTime.utc(2026, 11, 1),
      agentVersion: null,
      operatingSystemUpdateAvailable: osUpdate,
      operatingSystemLatestVersion: null,
      appUpdatesAvailableCount: appUpdates,
      unpatchedCveCount: 0,
      patchedCveCount: 0,
      removalRequested: false,
    );

PatchFailure _failure(
  String id,
  String hostId,
  String application, {
  bool os = false,
  String resolution = 'Outstanding',
  int count = 2,
}) =>
    patchFailureFromJson({
      'id': id,
      'hostId': hostId,
      'hostname': 'host-$hostId',
      'applicationName': application,
      'details': 'exit 1',
      'firstFailedUtc': '2026-11-01T02:00:00Z',
      'lastFailedUtc': '2026-11-05T02:00:00Z',
      'failureCount': count,
      'resolution': resolution,
      'hasScript': false,
      'canFix': false,
      if (os) ...{
        'isOperatingSystemUpdate': true,
        'displayName': 'Windows Update',
        'causeTitle': 'Not enough disk space',
        'causeExplanation': 'There was not enough room.',
        'suggestedFix': 'Free up space on C:.',
      },
    });

class _Failures implements PatchFailureRepository {
  _Failures(this.rows);

  final List<PatchFailure> rows;

  @override
  Future<List<PatchFailure>> list() async => rows;

  @override
  Future<void> dismiss(String id) async {}
}

class _Hosts implements HostRepository {
  _Hosts(this.rows);

  final List<HostSummary> rows;

  @override
  Future<List<HostSummary>> list() async => rows;

  @override
  noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _Applications implements ApplicationRepository {
  final sent = <({String application, String platform, List<String> hosts})>[];

  @override
  Future<ForcedPatchRunResult> forcePatchRuns({
    required String applicationName,
    required String platform,
    required List<String> hostNames,
  }) async {
    sent.add((application: applicationName, platform: platform, hosts: hostNames));
    return ForcedPatchRunResult(requested: hostNames.length, notRequested: const []);
  }

  @override
  noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  group('who can be patched now', () {
    HealthItem item(HealthKind kind, String os, {bool pending = false}) => HealthItem(
          key: 'k',
          kind: kind,
          hostId: 'h',
          hostname: 'host',
          operatingSystem: os,
          subject: 's',
          cause: 'c',
          explanation: 'e',
          suggestedFix: 'f',
          tries: 1,
          since: null,
          details: null,
          osUpdatePending: pending,
        );

    test('a Windows host failing its operating system update can', () {
      expect(item(HealthKind.osUpdate, 'Windows 11 Pro', pending: true).canPatchNow, isTrue);
    });

    test('an offline Windows host with an update waiting can, and without one cannot', () {
      expect(item(HealthKind.offline, 'Windows 11', pending: true).canPatchNow, isTrue);
      expect(item(HealthKind.offline, 'Windows 11').canPatchNow, isFalse);
    });

    test('an application failure cannot: it is repaired from Failed Updates', () {
      expect(item(HealthKind.application, 'Windows 11').canPatchNow, isFalse);
    });

    test('a macOS or Linux host cannot yet, since their agents cannot act on it', () {
      expect(item(HealthKind.osUpdate, 'macOS 15.0', pending: true).canPatchNow, isFalse);
      expect(item(HealthKind.osUpdate, 'Ubuntu 24.04', pending: true).canPatchNow, isFalse);
    });
  });

  group('buildHealthItems', () {
    test('an operating system failure carries the server\'s reason', () {
      final items = buildHealthItems(
        failures: [_failure('f1', 'h1', 'Windows', os: true)],
        hosts: [_host('h1', 'host-h1')],
      );

      expect(items, hasLength(1));
      expect(items.single.kind, HealthKind.osUpdate);
      expect(items.single.subject, 'Windows Update');
      expect(items.single.cause, 'Not enough disk space');
      expect(items.single.suggestedFix, 'Free up space on C:.');
      expect(items.single.tries, 2);
    });

    test('an application failure gets an honest generic reason', () {
      final items = buildHealthItems(
        failures: [_failure('f1', 'h1', 'Slack')],
        hosts: [_host('h1', 'host-h1')],
      );

      expect(items.single.kind, HealthKind.application);
      expect(items.single.cause, 'The upgrade script failed');
    });

    test('a settled failure is not listed', () {
      final items = buildHealthItems(
        failures: [_failure('f1', 'h1', 'Slack', resolution: 'Dismissed')],
        hosts: [_host('h1', 'host-h1')],
      );

      expect(items, isEmpty);
    });

    test('an offline host with updates waiting is listed, and an online one is not', () {
      final items = buildHealthItems(
        failures: const [],
        hosts: [
          _host('h1', 'asleep', status: HostStatus.offline, osUpdate: true),
          _host('h2', 'fine', osUpdate: true),
          _host('h3', 'gone-and-current', status: HostStatus.offline, osUpdate: false),
        ],
      );

      expect(items.map((i) => i.hostname), ['asleep']);
      expect(items.single.kind, HealthKind.offline);
    });

    test('a host already listed for an OS failure is not listed again as offline', () {
      final items = buildHealthItems(
        failures: [_failure('f1', 'h1', 'Windows', os: true)],
        hosts: [_host('h1', 'host-h1', status: HostStatus.offline, osUpdate: true)],
      );

      expect(items, hasLength(1));
      expect(items.single.kind, HealthKind.osUpdate);
    });

    test('the worst come first: operating system, then applications, then offline', () {
      final items = buildHealthItems(
        failures: [_failure('a', 'h2', 'Slack'), _failure('o', 'h1', 'Windows', os: true)],
        hosts: [
          _host('h1', 'host-h1'),
          _host('h2', 'host-h2'),
          _host('h3', 'asleep', status: HostStatus.offline, appUpdates: 1),
        ],
      );

      expect(items.map((i) => i.kind), [HealthKind.osUpdate, HealthKind.application, HealthKind.offline]);
    });

    test('a host failing twice counts once among the hosts needing attention', () {
      final items = buildHealthItems(
        failures: [_failure('a', 'h1', 'Slack'), _failure('o', 'h1', 'Windows', os: true)],
        hosts: [_host('h1', 'host-h1')],
      );

      expect(items, hasLength(2));
      expect(hostsNeedingAttention(items), 1);
    });
  });

  group('the screen', () {
    late _Applications applications;

    setUp(() => applications = _Applications());
    tearDown(() => locator.reset());

    Future<void> pumpScreen(WidgetTester tester) async {
      locator
        ..registerSingleton(RequestForcedPatchRuns(applications))
        ..registerSingleton(GetPatchFailures(_Failures([
          _failure('o', 'h1', 'Windows', os: true),
          _failure('a', 'h2', 'Slack'),
        ])))
        ..registerSingleton(GetHosts(_Hosts([
          _host('h1', 'host-h1'),
          _host('h2', 'host-h2'),
          _host('h3', 'asleep-host', status: HostStatus.offline, osUpdate: true),
        ])));

      tester.view.physicalSize = const Size(1500, 1200);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: PatchHealthScreen())),
      );
      await tester.pumpAndSettle();
    }

    testWidgets('lists the failing hosts with the reason, and the tabs count them', (tester) async {
      await pumpScreen(tester);

      expect(find.text('host-h1'), findsOneWidget);
      expect(find.text('host-h2'), findsOneWidget);
      expect(find.text('asleep-host'), findsOneWidget);
      expect(find.text('NOT ENOUGH DISK SPACE'), findsOneWidget);
      expect(find.text('ALL (3)'), findsOneWidget);
      expect(find.text('OPERATING SYSTEM (1)'), findsOneWidget);
      expect(find.text('OFFLINE (1)'), findsOneWidget);
    });

    testWidgets('a tab leaves only its kind', (tester) async {
      await pumpScreen(tester);

      await tester.tap(find.text('OFFLINE (1)'));
      await tester.pumpAndSettle();

      expect(find.text('asleep-host'), findsOneWidget);
      expect(find.text('host-h1'), findsNothing);
    });

    testWidgets('a row opens to its explanation and what to do', (tester) async {
      await pumpScreen(tester);

      await tester.tap(find.byTooltip('Show the details').first);
      await tester.pumpAndSettle();

      expect(find.text('There was not enough room.'), findsOneWidget);
      expect(find.text('OPEN IN FAILED UPDATES'), findsOneWidget);
    });

    testWidgets('Select all then Patch selected asks to confirm, then sends one instruction per host', (tester) async {
      await pumpScreen(tester);

      await tester.tap(find.text('Select all'));
      await tester.pumpAndSettle();
      expect(find.text('PATCH SELECTED (2)'), findsOneWidget, reason: 'the offline Windows host and the failing one');

      await tester.tap(find.text('PATCH SELECTED (2)'));
      await tester.pumpAndSettle();
      expect(find.textContaining('5-minute'), findsOneWidget);
      expect(applications.sent, isEmpty, reason: 'nothing is sent until it is confirmed');

      await tester.tap(find.text('PATCH 2 HOSTS NOW'));
      await tester.pumpAndSettle();

      expect(applications.sent, hasLength(1));
      expect(applications.sent.single.application, 'Windows');
      expect(applications.sent.single.platform, 'Windows');
      expect(applications.sent.single.hosts, unorderedEquals(['host-h1', 'asleep-host']));
      expect(find.textContaining('Patch now sent to 2 hosts'), findsOneWidget);
    });

    testWidgets('cancelling the dialog sends nothing', (tester) async {
      await pumpScreen(tester);

      await tester.tap(find.byTooltip('Patch now').first);
      await tester.pumpAndSettle();
      await tester.tap(find.text('CANCEL'));
      await tester.pumpAndSettle();

      expect(applications.sent, isEmpty);
    });

    testWidgets('an application failure has no checkbox to tick and no Patch now', (tester) async {
      await pumpScreen(tester);

      final checkboxes = tester.widgetList<Checkbox>(find.byType(Checkbox)).toList();
      // Select all, then one per row: operating system, application, offline.
      expect(checkboxes, hasLength(4));
      expect(checkboxes[2].onChanged, isNull, reason: 'the Slack failure');
      expect(find.byTooltip('Patch now'), findsNWidgets(2));
    });
  });
}
