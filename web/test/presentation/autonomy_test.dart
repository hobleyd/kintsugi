import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/platform/file_saver.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/status_chip.dart';
import 'package:kintsugi_web/data/models/autonomy_mapper.dart';
import 'package:kintsugi_web/data/models/host_activity_mapper.dart';
import 'package:kintsugi_web/domain/entities/autonomy.dart';
import 'package:kintsugi_web/domain/entities/host_activity.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/autonomy_usecases.dart';
import 'package:kintsugi_web/domain/usecases/host_activity_usecases.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_model.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_pdf.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_report.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_screen.dart';

Map<String, dynamic> _cause(String key, String title, String category, int devices, double days, int share,
        {String? why, String? fix, int connected = 0, int offline = 0, bool measured = true}) =>
    {
      'key': key,
      'title': title,
      'category': category,
      'measured': measured,
      'devices': devices,
      'deviceDays': days,
      'sharePercent': share,
      'explanation': why,
      'suggestedFix': fix,
      'patchNowConnected': connected,
      'patchNowOffline': offline,
    };

Map<String, dynamic> _kpi(String id, String def, double value, String status,
        {double? prior, double? delta, String direction = 'LowerIsBetter', double green = 2, double amber = 5}) =>
    {
      'id': id,
      'category': 'Failures',
      'definition': def,
      'direction': direction,
      'isPercent': true,
      'green': green,
      'amber': amber,
      'value': value,
      'numerator': 8,
      'denominator': 100,
      'status': status,
      'prior': prior,
      'delta': delta,
    };

Map<String, dynamic> _json({int connected = 14, bool history = false, String period = '30d', String? month}) => {
      'period': period,
      'fromUtc': '2026-10-08T00:00:00Z',
      'toUtc': '2026-11-06T12:00:00Z',
      'month': month,
      'historyStartsUtc': '2026-06-06T00:00:00Z',
      'hostsWithEvents': 90,
      'totalHosts': 100,
      'devicesDelayed': 20,
      'totalDeviceDays': 200.0,
      'patches': {'total': 40, 'ownSchedule': 36, 'adminStarted': 4},
      'buckets': [
        {'startUtc': '2026-11-04T00:00:00Z', 'own': 3, 'admin': 1},
        {'startUtc': '2026-11-05T00:00:00Z', 'own': 5, 'admin': 0},
      ],
      'months': [
        {'month': '2026-09', 'own': 120, 'admin': 10},
        {'month': '2026-10', 'own': 150, 'admin': 12},
        {'month': '2026-11', 'own': 36, 'admin': 4},
      ],
      'causes': [
        _cause('disconnected', 'Disconnected: has not checked in', 'Disconnected', 8, 100.0, 50,
            why: 'The device is off.', fix: 'Check it is on.'),
        _cause('failure:windows-update-error-0x80070643', 'Windows Update error 0x80070643', 'Failure', 9, 60.0, 30,
            why: 'The update service could not finish.', fix: 'Restart the Windows Update service.', connected: connected, offline: 2),
        _cause('restart', 'Updated, restart pending', 'Restart', 3, 40.0, 20, why: 'Not restarted.'),
        _cause('maintenance', 'In maintenance mode', 'Maintenance', 0, 0.0, 0, measured: false),
      ],
      'kpis': [
        _kpi('failures.open_failure_devices', 'Devices with an open failed update', 8.0, 'R', prior: 5.0, delta: 3.0),
        _kpi('patching.os_update_waiting', 'Devices with an OS update waiting', 4.0, 'G', prior: 6.0, delta: -2.0, green: 5, amber: 10),
        _kpi('reachability.checked_in_24h', 'Devices that checked in within 24 hours', 96.0, 'A',
            direction: 'HigherIsBetter', green: 98, amber: 95),
        _kpi('patching.admin_started_share', 'Patches started by an administrator (for information)', 10.0, 'I', direction: 'Information', green: 0, amber: 0),
      ],
      'kpiHistory': history
          ? [
              {
                'id': 'failures.open_failure_devices',
                'points': [
                  {'dateUtc': '2026-11-01T00:00:00Z', 'value': 5.0},
                  {'dateUtc': '2026-11-02T00:00:00Z', 'value': 6.5},
                  {'dateUtc': '2026-11-03T00:00:00Z', 'value': 8.0},
                ],
              },
            ]
          : [],
      'deviceHealth': {
        'unstable': 1,
        'watch': 1,
        'items': [
          {
            'hostId': 'hh1',
            'hostname': 'laptop-ceo',
            'status': 'Unstable',
            'why': '4 unexpected shutdowns in the last 7 days, 17 in 30 days. 10 fatal hardware errors in 30 days',
            'lastEventUtc': '2026-11-06T04:04:00Z',
          },
          {
            'hostId': 'hh2',
            'hostname': 'design-win-02',
            'status': 'Watch',
            'why': '1 unexpected shutdown in the last 7 days, 1 in 30 days',
            'lastEventUtc': '2026-11-03T09:12:00Z',
          },
        ],
      },
      'events': [
        {'occurredUtc': '2026-11-05T14:00:00Z', 'kind': 'PatchNowRequested', 'subject': 'Windows', 'hostname': 'pc-1', 'actor': 'sara@example.com', 'count': 1},
      ],
    };

class _Repo implements AutonomyRepository {
  _Repo(this.json);

  Map<String, dynamic> json;
  final asked = <(AutonomyPeriod, String?)>[];
  final patched = <(String, int, bool)>[];
  ApiException? patchError;

  @override
  Future<Autonomy> get(AutonomyPeriod period, {String? month}) async {
    asked.add((period, month));
    return autonomyFromJson({...json, 'period': period.wire, 'month': month});
  }

  @override
  Future<AutonomyDevicesPage> devices(String key, {int skip = 0, int take = 25, String? search}) async =>
      AutonomyDevicesPage(total: 9, items: [
        for (var i = 0; i < 3; i++)
          AutonomyDevice(hostId: 'h$i', hostname: 'pc-$i', detail: 'failed 4 times', connected: true, patchNowEligible: true),
      ]);

  @override
  Future<AutonomyPatchNowResult> patchNow(String key, {required int expectedConnected, required bool includeOffline}) async {
    if (patchError != null) throw patchError!;
    patched.add((key, expectedConnected, includeOffline));
    return AutonomyPatchNowResult(started: expectedConnected, queuedOffline: includeOffline ? 2 : 0, notRequested: const []);
  }
}

class _Activity implements HostActivityRepository {
  @override
  Future<HostActivity> get(String hostId) async => hostActivityFromJson({
        'hostId': hostId,
        'hostname': 'laptop-ceo',
        'status': 'Online',
        'retentionMonths': 6,
        'events': [
          {'id': 'e1', 'occurredUtc': '2026-11-06T04:04:00Z', 'kind': 'UnexpectedShutdown', 'subject': 'Kernel-Power 41', 'detail': 'BugcheckCode 0', 'count': 1},
          {'id': 'e2', 'occurredUtc': '2026-11-06T03:50:00Z', 'kind': 'HardwareErrorFatal', 'subject': 'WHEA-Logger 1', 'count': 1},
          {'id': 'e3', 'occurredUtc': '2026-11-03T10:00:00Z', 'kind': 'ApplicationPatched', 'subject': 'Firefox', 'count': 1},
        ],
        'pendingUpdates': [],
        'openFailures': [],
        'patchedLast30Days': 0,
      });

  @override
  Future<int> setRetention(int months) async => months;
}

class _Saver implements FileSaver {
  final saved = <(Uint8List, String, String?)>[];

  @override
  Future<void> save(Uint8List bytes, String fileName, {String mimeType = 'application/octet-stream'}) async =>
      saved.add((bytes, fileName, mimeType));
}

String _words(ReportDocument d) => [
      for (final s in d.sections) ...[
        s.title,
        for (final b in s.blocks)
          switch (b) {
            ReportText() => b.text,
            ReportBullets() => b.items.join(' '),
            ReportStats() => b.items.map((i) => '${i.$1} ${i.$2}').join(' '),
            ReportTable() => [b.headers.join(' '), ...b.rows.map((r) => r.join(' '))].join(' '),
            ReportColumns() => b.title,
            ReportBars() => '${b.title} ${b.items.map((i) => '${i.$1} ${i.$2}').join(' ')}',
            ReportLine() => b.title,
          },
      ],
    ].join('\n');

void main() {
  final data = autonomyFromJson(_json());

  group('the mapper', () {
    test('reads causes with their cost, KPIs with their bands, and no device list', () {
      expect(data.causes.first.key, 'disconnected');
      expect(data.causes[1].deviceDays, 60.0);
      expect(data.causes[1].canPatchNow, isTrue);
      expect(data.causes[1].isFailure, isTrue);
      expect(data.causes[3].measured, isFalse);
      expect(data.kpis.first.status, 'R');
      expect(data.kpis.first.delta, 3.0);
      expect(data.devicesDelayed, 20);
    });

    test('a share of zero patches is zero, not a division by zero', () {
      expect(const AutonomyPatches(total: 0, ownSchedule: 0, adminStarted: 0).percent(0), 0);
    });

    test('an unknown period falls back to 30 days', () {
      expect(autonomyPeriodFromJson('nonsense'), AutonomyPeriod.days30);
    });

    test('reads the device health: counts of the whole fleet and the worst rows', () {
      expect((data.deviceHealth.unstable, data.deviceHealth.watch), (1, 1));
      expect(data.deviceHealth.items.first.hostname, 'laptop-ceo');
      expect(data.deviceHealth.items.first.isUnstable, isTrue);
      expect(data.deviceHealth.items.last.isUnstable, isFalse);
    });

    test('an older server with no device health reads as none', () {
      final json = _json()..remove('deviceHealth');

      expect(autonomyFromJson(json).deviceHealth, AutonomyDeviceHealth.none);
    });

    test('reads a page of devices and a Patch now result', () {
      final page = autonomyDevicesPageFromJson({
        'total': 2,
        'items': [
          {'hostId': 'a', 'hostname': 'pc-a', 'detail': 'd', 'connected': true, 'patchNowEligible': true},
        ],
      });
      final result = autonomyPatchNowResultFromJson({'started': 3, 'queuedOffline': 1, 'notRequested': ['gone']});

      expect(page.items.single.patchNowEligible, isTrue);
      expect((result.started, result.queuedOffline), (3, 1));
      expect(result.notRequested, ['gone']);
    });
  });

  group('the model', () {
    test('bands read the way the direction goes', () {
      expect(bandsOf(data.kpis[0]).green, '≤2%');
      expect(bandsOf(data.kpis[0]).red, '>5%');
      expect(bandsOf(data.kpis[2]).green, '≥98%');
      expect(bandsOf(data.kpis[2]).red, '<95%');
      expect(bandsOf(data.kpis[3]).green, '—');
    });

    test('values and changes are written plainly', () {
      expect(kpiValue(data.kpis[0]), '8%');
      expect(kpiChange(data.kpis[0]), '▲3.0 pp');
      expect(kpiChange(data.kpis[1]), '▼2.0 pp');
      expect(kpiChange(data.kpis[2]), '—');
    });

    /// The ranking is by what it costs in delay, not by how many devices: 8 offline devices cost more than 9
    /// failing ones here.
    test('the actions to be faster come in order of the delay they recover', () {
      final actions = speedActions(data);

      expect(actions.map((a) => a.deviceDays), [100.0, 60.0, 40.0]);
      expect(actions[1].title, contains('Patch now'));
      expect(actions[1].effort, contains('all 16 together'));
    });

    test('maintenance mode is never an action: it is not measured', () {
      expect(speedActions(data).any((a) => a.title.contains('maintenance')), isFalse);
    });
  });

  group('was it Kintsugi?', () {
    HostEvent event(String kind, String at, {String subject = 'Windows'}) => hostEventFromJson({
          'id': at,
          'occurredUtc': at,
          'kind': kind,
          'subject': subject,
          'count': 1,
        });

    final shutdown = event('UnexpectedShutdown', '2026-11-06T14:04:00Z', subject: 'Kernel-Power 41');

    test('says nothing of ours happened when nothing did', () {
      expect(kintsugiNear(shutdown, [shutdown, event('ApplicationPatched', '2026-11-03T10:00:00Z')]), 'No Kintsugi action within 15 minutes');
    });

    test('names a patch just before it, with how long before', () {
      final patch = event('OperatingSystemPatched', '2026-11-06T13:58:00Z');

      expect(kintsugiNear(shutdown, [shutdown, patch]), 'Kintsugi: Patched Windows (6 minutes before)');
    });

    test('names a Patch now just after it', () {
      final now = event('PatchNowRequested', '2026-11-06T14:09:00Z');

      expect(kintsugiNear(shutdown, [shutdown, now]), contains('Patch now requested for Windows (5 minutes after)'));
    });

    test('another health event or an administrator dismissing a failure is not Kintsugi acting on the device', () {
      final other = event('HardwareErrorCorrected', '2026-11-06T14:02:00Z');
      final dismissed = event('FailureDismissed', '2026-11-06T14:03:00Z');

      expect(kintsugiNear(shutdown, [shutdown, other, dismissed]), 'No Kintsugi action within 15 minutes');
    });

    test('the new event kinds read, group and summarise', () {
      expect(hostEventKindFromJson('UnexpectedShutdown'), HostEventKind.unexpectedShutdown);
      expect(HostEventKind.hardwareErrorFatal.group, HostEventGroup.health);
      expect(shutdown.summary, 'Shut down without being asked (Kernel-Power 41)');
    });
  });

  group('the report', () {
    ReportDocument build(ReportOptions o, {Autonomy? d}) => buildAutonomyReport(d ?? data, o, DateTime(2026, 11, 6));

    test('has the sections that were chosen and no others', () {
      final doc = build(const ReportOptions(summary: true, kpis: false, causes: true, health: false, faster: false, trends: false, chart: false));

      expect(doc.sections.map((s) => s.title), ['Executive summary', 'Why patching is slow, ranked by what it costs']);
    });

    test('lists causes and no devices, whatever the fleet', () {
      final text = _words(build(const ReportOptions()));

      expect(text, contains('Windows Update error 0x80070643'));
      expect(text, contains('Disconnected'));
      expect(text, isNot(contains('pc-1')));
      expect(text, isNot(contains('pc-0')));
    });

    test('ranks causes by delay and gives the why, how it slows and how to remediate', () {
      final causes = build(const ReportOptions()).sections.firstWhere((s) => s.title.startsWith('Why patching'));
      final table = causes.blocks.whereType<ReportTable>().single;

      expect(table.headers, ['Cause', 'Devices', 'Share of delay', 'Why it happens', 'How it slows patching', 'How to remediate']);
      expect(table.rows.map((r) => r[2]), ['50%', '30%', '20%']);
      expect(table.rows[1][3], 'The update service could not finish.');
    });

    test('has a device health section with counts, the rule, and no device names', () {
      final health = build(const ReportOptions()).sections.firstWhere((s) => s.title == 'Device health');
      final text = _words(ReportDocument(title: '', subtitle: '', sections: [health]));

      expect(text, contains('1 unstable'));
      expect(text, contains('Unstable: 3 or more unexpected shutdowns in 7 days'));
      expect(text, isNot(contains('laptop-ceo')));
      expect(text, contains('hardware fault'));
    });

    test('draws the cost as bars', () {
      final causes = build(const ReportOptions()).sections.firstWhere((s) => s.title.startsWith('Why patching'));

      expect(causes.blocks.whereType<ReportBars>().single.items.first, ('Disconnected: has not checked in', 100.0));
    });

    test('the KPI table colours status as a word as well', () {
      final kpis = build(const ReportOptions()).sections.firstWhere((s) => s.title.startsWith('KPIs'));
      final table = kpis.blocks.whereType<ReportTable>().single;

      expect(table.rows.map((r) => r.last), ['RED', 'GREEN', 'AMBER', 'INFO']);
      expect(table.statuses[8], ['R', 'G', 'A', 'I']);
      expect(table.headers.first, 'KPI');
      expect(table.rows.first.first, 'Devices with an open failed update');
    });

    test('says history is building when there are fewer than two days of snapshots', () {
      final trends = build(const ReportOptions()).sections.firstWhere((s) => s.title.startsWith('Trends'));

      expect(trends.blocks.whereType<ReportLine>(), isEmpty);
      expect(_words(build(const ReportOptions())), contains('History is building'));
    });

    test('draws a trend against its target once there is history', () {
      final withHistory = autonomyFromJson(_json(history: true));
      final trends = build(const ReportOptions(), d: withHistory).sections.firstWhere((s) => s.title.startsWith('Trends'));

      final line = trends.blocks.whereType<ReportLine>().single;
      expect(line.values, [5.0, 6.5, 8.0]);
      expect(line.target, 2.0);
      expect(line.targetLabel, 'target ≤2%');
    });

    test('prints the commentary only when something was written', () {
      expect(build(const ReportOptions()).sections.any((s) => s.title == 'Commentary'), isFalse);
      expect(_words(build(const ReportOptions(commentary: 'Fix rolling out.'))), contains('Fix rolling out.'));
    });

    test('explains the rule that decides who started a patch', () {
      expect(_words(build(const ReportOptions(chart: true))), contains('started by an administrator'));
    });
  });

  group('the PDF', () {
    Future<String> textOf(Uint8List bytes) async {
      final words = <String>[];
      final stream = RegExp(r'stream\r?\n(.*?)\r?\nendstream', dotAll: true);
      for (final m in stream.allMatches(latin1.decode(bytes))) {
        try {
          final inflated = latin1.decode(zlib.decode(latin1.encode(m.group(1)!)));
          words.addAll(RegExp(r'\[\((.*?)\)\]TJ').allMatches(inflated).map((x) => x.group(1)!));
        } on Object {
          // Not a compressed text stream.
        }
      }
      return words.join(' ');
    }

    test('is a PDF with the report in it, charts included', () async {
      final withHistory = autonomyFromJson(_json(history: true));
      final bytes = await reportToPdf(buildAutonomyReport(withHistory, const ReportOptions(commentary: 'Fix rolling out.'), DateTime(2026, 11, 6)));

      expect(ascii.decode(bytes.sublist(0, 5)), '%PDF-');
      final text = await textOf(bytes);
      expect(text, contains('Patching report'));
      expect(text, contains('Device-days of delay by cause'));
      expect(text, contains('Windows Update error 0x80070643'));
      expect(text, contains('Patches per day'));
      expect(text, contains('Fix rolling out.'));
    });

    test('replaces what the built-in font cannot draw', () {
      expect(pdfSafe('a → b … c – d'), 'a -> b ... c - d');
      expect(pdfSafe('≤2% ≥98% ▲1 ▼2'), '<=2% >=98% +1 -2');
      expect(pdfSafe('café · ok'), 'café · ok');
      expect(pdfSafe('日本'), '??');
    });
  });

  group('the screen', () {
    late _Repo repo;
    late _Saver saver;

    setUp(() {
      repo = _Repo(_json());
      saver = _Saver();
    });
    tearDown(() => locator.reset());

    Future<void> pump(WidgetTester tester) async {
      locator
        ..registerSingleton(GetAutonomy(repo))
        ..registerSingleton(GetAutonomyCauseDevices(repo))
        ..registerSingleton(PatchAutonomyCause(repo))
        ..registerSingleton(GetHostActivity(_Activity()))
        ..registerSingleton<FileSaver>(saver);
      tester.view.physicalSize = const Size(1500, 3400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: AutonomyScreen())));
      await tester.pumpAndSettle();
    }

    testWidgets('opens on the last 30 days with causes ranked by cost, the biggest open', (tester) async {
      await pump(tester);

      expect(repo.asked.first.$1, AutonomyPeriod.days30);
      expect(find.text('WHY PATCHING IS SLOW, RANKED BY WHAT IT COSTS'), findsOneWidget);
      expect(find.text('Disconnected: has not checked in'), findsOneWidget);
      expect(find.text('HOW TO REMEDIATE'), findsOneWidget, reason: 'only the costliest cause is open');
      expect(find.text('Check it is on.'), findsOneWidget, reason: 'Disconnected costs the most in this fleet');
      expect(find.text('NOT BUILT YET'), findsOneWidget);
    });

    testWidgets('lists the unhealthy devices with their reason and a status word', (tester) async {
      await pump(tester);

      expect(find.text('DEVICE HEALTH'), findsOneWidget);
      expect(find.text('1 unstable'), findsOneWidget);
      expect(find.text('laptop-ceo'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, 'UNSTABLE'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, 'WATCH'), findsOneWidget);
      expect(find.textContaining('10 fatal hardware errors'), findsOneWidget);
    });

    testWidgets('opening a device shows its health events, and whether Kintsugi was involved', (tester) async {
      await pump(tester);

      await tester.tap(find.text('laptop-ceo'));
      await tester.pumpAndSettle();

      expect(find.text('Shut down without being asked (Kernel-Power 41)'), findsOneWidget);
      expect(find.text('Fatal hardware error (WHEA-Logger 1)'), findsOneWidget);
      expect(find.text('BugcheckCode 0'), findsOneWidget);
      expect(find.text('No Kintsugi action within 15 minutes'), findsWidgets);
      expect(find.text('Firefox'), findsNothing, reason: 'a patch is not a health event, so it is not listed');
    });

    testWidgets('shows the KPIs against their bands, with a word for the status', (tester) async {
      await pump(tester);

      expect(find.widgetWithText(StatusChip, 'RED'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, 'GREEN'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, 'AMBER'), findsOneWidget);
      expect(find.text('Devices with an open failed update'), findsOneWidget);
    });

    testWidgets('says history is building when there are no snapshots', (tester) async {
      await pump(tester);

      expect(find.textContaining('History is building'), findsOneWidget);
    });

    testWidgets('a period button asks the server for that period', (tester) async {
      await pump(tester);

      await tester.tap(find.text('LAST 7 DAYS'));
      await tester.pumpAndSettle();

      expect(repo.asked.last.$1, AutonomyPeriod.days7);
    });

    testWidgets('By month shows a chart of months, and tapping a month asks for it', (tester) async {
      await pump(tester);

      await tester.tap(find.text('BY MONTH'));
      await tester.pumpAndSettle();
      expect(find.text('PATCHES PER MONTH'), findsOneWidget);

      await tester.tap(find.text('Sep'));
      await tester.pumpAndSettle();

      expect(repo.asked.last, (AutonomyPeriod.month, '2026-09'));
    });

    testWidgets('Patch now confirms with the count, sends it, and says what happened', (tester) async {
      await pump(tester);
      await tester.tap(find.text('Windows Update error 0x80070643'));
      await tester.pumpAndSettle();

      await tester.tap(find.textContaining('PATCH NOW · 14 CONNECTED'));
      await tester.pumpAndSettle();
      expect(find.text('Patch 14 Windows devices now'), findsOneWidget);
      expect(repo.patched, isEmpty, reason: 'nothing is sent until it is confirmed');

      await tester.tap(find.text('PATCH 14 NOW'));
      await tester.pumpAndSettle();

      expect(repo.patched, [('failure:windows-update-error-0x80070643', 14, false)]);
      expect(find.textContaining('Patch now sent to 14 connected devices'), findsOneWidget);
    });

    testWidgets('the offline devices are queued only when asked', (tester) async {
      await pump(tester);
      await tester.tap(find.text('Windows Update error 0x80070643'));
      await tester.pumpAndSettle();
      await tester.tap(find.textContaining('PATCH NOW · 14 CONNECTED'));
      await tester.pumpAndSettle();

      await tester.tap(find.textContaining('Also queue the 2 offline'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('PATCH 14 NOW'));
      await tester.pumpAndSettle();

      expect(repo.patched.single.$3, isTrue);
    });

    /// A slip at 60 devices reaches a lot of people, so the number has to be typed.
    testWidgets('above 50 devices the number must be typed before it can be sent', (tester) async {
      repo = _Repo(_json(connected: 60));
      await pump(tester);
      await tester.tap(find.text('Windows Update error 0x80070643'));
      await tester.pumpAndSettle();
      await tester.tap(find.textContaining('PATCH NOW · 60 CONNECTED'));
      await tester.pumpAndSettle();

      expect(find.text('Type 60 to confirm'.toUpperCase()), findsOneWidget);
      await tester.tap(find.text('PATCH 60 NOW'));
      await tester.pumpAndSettle();
      expect(repo.patched, isEmpty);

      await tester.enterText(find.byType(TextField).last, '60');
      await tester.pumpAndSettle();
      await tester.tap(find.text('PATCH 60 NOW'));
      await tester.pumpAndSettle();

      expect(repo.patched.single.$2, 60);
    });

    testWidgets('a refusal because the list changed is shown, and nothing is claimed sent', (tester) async {
      repo.patchError = const ApiException('The list changed while it was open.', statusCode: 409);
      await pump(tester);
      await tester.tap(find.text('Windows Update error 0x80070643'));
      await tester.pumpAndSettle();
      await tester.tap(find.textContaining('PATCH NOW · 14 CONNECTED'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('PATCH 14 NOW'));
      await tester.pumpAndSettle();

      expect(find.text('The list changed while it was open.'), findsOneWidget);
      expect(find.textContaining('Patch now sent to'), findsNothing);
    });

    testWidgets('a cause Patch now cannot act on offers no button', (tester) async {
      await pump(tester);

      // Disconnected is open first and has no button: its devices are not connected Windows ones.
      expect(find.textContaining('PATCH NOW · '), findsNothing);

      await tester.tap(find.text('Updated, restart pending'));
      await tester.pumpAndSettle();
      expect(find.textContaining('PATCH NOW · '), findsNothing, reason: 'Patch now does not restart a device that already installed everything');

      await tester.tap(find.text('Windows Update error 0x80070643'));
      await tester.pumpAndSettle();
      expect(find.textContaining('PATCH NOW · '), findsOneWidget);
    });

    testWidgets('the devices in a cause are read only when asked for', (tester) async {
      await pump(tester);
      expect(find.text('pc-0'), findsNothing);

      await tester.tap(find.text('Show devices (8)'));
      await tester.pumpAndSettle();

      expect(find.text('pc-0'), findsOneWidget);
      expect(find.text('Showing 3 of 9'), findsOneWidget);
    });

    testWidgets('the Report tab shows the report and downloads it as a PDF', (tester) async {
      await pump(tester);

      await tester.tap(find.text('REPORT'));
      await tester.pumpAndSettle();
      expect(find.text('Patching report'), findsOneWidget);
      expect(find.text('Why patching is slow, ranked by what it costs'), findsOneWidget);

      await tester.tap(find.text('DOWNLOAD PDF'));
      await tester.pumpAndSettle();

      expect(saver.saved, hasLength(1));
      expect(saver.saved.single.$2, startsWith('kintsugi-patching-report-'));
      expect(saver.saved.single.$3, 'application/pdf');
      expect(ascii.decode(saver.saved.single.$1.sublist(0, 5)), '%PDF-');
      expect(find.textContaining('saved as a PDF'), findsOneWidget);
    });

    testWidgets('what is written as commentary appears in the report', (tester) async {
      await pump(tester);
      await tester.tap(find.text('REPORT'));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField).last, 'A fix is rolling out.');
      await tester.pumpAndSettle();

      expect(find.text('A fix is rolling out.'), findsWidgets);
    });
  });
}
