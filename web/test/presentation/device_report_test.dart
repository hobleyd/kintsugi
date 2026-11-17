import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_client.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/network/unauthorized_notifier.dart';
import 'package:kintsugi_web/core/platform/file_saver.dart';
import 'package:kintsugi_web/core/router/app_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/core/widgets/status_chip.dart';
import 'package:kintsugi_web/data/models/autonomy_mapper.dart';
import 'package:kintsugi_web/data/models/device_report_mapper.dart';
import 'package:kintsugi_web/data/models/host_activity_mapper.dart';
import 'package:kintsugi_web/data/repositories/device_report_repository_impl.dart';
import 'package:kintsugi_web/domain/entities/autonomy.dart';
import 'package:kintsugi_web/domain/entities/device_report.dart';
import 'package:kintsugi_web/domain/entities/host_activity.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/autonomy_usecases.dart';
import 'package:kintsugi_web/domain/usecases/device_report_usecases.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_pdf.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_report.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_screen.dart';
import 'package:kintsugi_web/presentation/autonomy/device_health_row.dart';
import 'package:kintsugi_web/presentation/autonomy/device_report_pdf_model.dart';
import 'package:kintsugi_web/presentation/autonomy/device_report_screen.dart';
import 'package:kintsugi_web/presentation/autonomy/device_search.dart';

String _ago(Duration d) => DateTime.now().toUtc().subtract(d).toIso8601String();

Map<String, dynamic> _log(
  String status, {
  String id = 'c1',
  String detail = 'WarningsAndErrors',
  int days = 30,
  String? failure,
  String? truncatedBefore,
}) =>
    {
      'id': id,
      'status': status,
      'requestedUtc': _ago(const Duration(minutes: 20)),
      'requestedBy': 'sara@example.com',
      'periodDays': days,
      'detail': detail,
      'expiresUtc': _ago(const Duration(hours: -24)),
      'claimedUtc': status == 'Requested' ? null : _ago(const Duration(minutes: 10)),
      'completedUtc': status == 'Completed' ? _ago(const Duration(minutes: 5)) : null,
      'failureReason': failure,
      'entryCount': status == 'Completed' ? 4210 : 0,
      'sizeBytes': status == 'Completed' ? 2621440 : 0,
      'truncatedBeforeUtc': truncatedBefore,
      'keepUntilUtc': _ago(const Duration(days: -29)),
    };

Map<String, dynamic> _reportJson({Map<String, dynamic>? logs, int days = 7}) => {
      'host': {
        'id': 'h1',
        'hostname': 'LAPTOP-CEO-01',
        'operatingSystem': 'Windows 11 Pro 23H2',
        'platform': 'Windows',
        'serialNumber': '7XK2M93',
        'agentVersion': '0.12.6',
        'lastSeenUtc': _ago(const Duration(minutes: 2)),
      },
      'days': days,
      'health': {
        'status': 'Unstable',
        'why': '2 unexpected shutdowns in the last 7 days, 2 in 30 days. 1 fatal hardware error in 30 days',
        'shutdowns7d': 2,
        'shutdowns30d': 2,
        'fatalErrors30d': 1,
        'correctedErrors30d': 1,
      },
      'events': [
        {
          'occurredUtc': _ago(const Duration(hours: 52)),
          'kind': 'HardwareErrorCorrected',
          'source': 'WHEA-Logger 19',
          'detail': 'A corrected hardware error occurred. Component: Memory',
          'kintsugiNearby': null,
        },
        {
          'occurredUtc': _ago(const Duration(hours: 30)),
          'kind': 'UnexpectedShutdown',
          'source': 'EventLog 6008',
          'detail': 'The previous system shutdown was unexpected',
          'kintsugiNearby': {
            'action': 'Install',
            'subject': 'Google Chrome 130.0',
            'atUtc': _ago(const Duration(hours: 34)),
            'minutesBefore': 270.0,
            'within30Minutes': false,
          },
        },
        {
          'occurredUtc': _ago(const Duration(hours: 5)),
          'kind': 'UnexpectedShutdown',
          'source': 'Kernel-Power 41',
          'detail': 'The system rebooted without cleanly shutting down first',
          'kintsugiNearby': {
            'action': 'Install',
            'subject': 'Zoom 6.2.5',
            'atUtc': _ago(const Duration(hours: 5, minutes: 12)),
            'minutesBefore': 12.0,
            'within30Minutes': true,
          },
        },
        {
          'occurredUtc': _ago(const Duration(hours: 5)),
          'kind': 'HardwareErrorFatal',
          'source': 'WHEA-Logger 18',
          'detail': 'A fatal hardware error occurred. Component: Processor Core',
          'kintsugiNearby': null,
        },
      ],
      'kintsugiActions': [
        {'atUtc': _ago(const Duration(hours: 5, minutes: 12)), 'kind': 'Install', 'subject': 'Zoom 6.2.5'},
        {'atUtc': _ago(const Duration(hours: 100)), 'kind': 'OsUpdate', 'subject': 'KB5034441'},
      ],
      'shutdownsNearKintsugi': 1,
      'hypotheses': [
        {
          'title': 'Hardware fault in the processor or memory',
          'confidence': 'High',
          'evidence': ['1 fatal hardware error (WHEA-Logger 18) in 30 days.'],
          'checkNext': ['Run the maker\'s diagnostics and a memory test.'],
        },
        {
          'title': 'Kintsugi patching',
          'confidence': 'Unlikely',
          'evidence': ['Only 1 of 2 shutdowns came within 30 minutes of an install.'],
          'checkNext': ['Nothing to do on the patching side.'],
        },
      ],
      'installRuns': [
        {'atUtc': _ago(const Duration(hours: 5, minutes: 12)), 'subject': 'Zoom 6.2.5', 'succeeded': true, 'output': null},
        {
          'atUtc': _ago(const Duration(hours: 48)),
          'subject': 'Adobe Acrobat Reader 24.002',
          'succeeded': false,
          'output': 'ERROR 1603: Fatal error during installation.',
        },
      ],
      'logs': logs,
    };

class _EntryCall {
  _EntryCall(this.severity, this.search, this.around, this.aroundMinutes, this.skip, this.take);

  final LogSeverityFilter severity;
  final String? search;
  final DateTime? around;
  final int aroundMinutes;
  final int skip;
  final int take;
}

class _Repo implements DeviceReportRepository {
  _Repo(this.json);

  Map<String, dynamic> json;
  final getDays = <int>[];
  final requested = <(int, LogDetail)>[];
  final deleted = <String>[];
  final entryCalls = <_EntryCall>[];
  ApiException? requestError;

  void setLogs(Map<String, dynamic>? logs) => json = {...json, 'logs': logs};

  @override
  Future<DeviceReport> get(String hostId, {int days = 7}) async {
    getDays.add(days);
    return deviceReportFromJson({...json, 'days': days});
  }

  @override
  Future<LogCollection?> latestLogs(String hostId) async =>
      logCollectionOrNullFromJson(json['logs']);

  @override
  Future<LogCollection> requestLogs(String hostId, {required int periodDays, required LogDetail detail}) async {
    if (requestError != null) throw requestError!;
    requested.add((periodDays, detail));
    final created = _log('Requested', id: 'new', days: periodDays, detail: detail.wire);
    setLogs(created);
    return logCollectionFromJson(created);
  }

  @override
  Future<void> deleteLogs(String hostId, String id) async {
    deleted.add(id);
    // Only what was deleted goes: the newest request and the last completed log are separate rows.
    if ((json['logs'] as Map?)?['id'] == id) setLogs(null);
    if ((json['lastCompletedLogs'] as Map?)?['id'] == id) json = {...json, 'lastCompletedLogs': null};
  }

  /// The newest request is [newest] and the last collection that completed is [completed], as the server
  /// answers when a newer request has not (yet) produced a log.
  void setNewestAndLastCompleted(Map<String, dynamic> newest, Map<String, dynamic> completed) =>
      json = {...json, 'logs': newest, 'lastCompletedLogs': completed};

  @override
  Future<LogEntriesPage> entries(
    String hostId,
    String id, {
    LogSeverityFilter severity = LogSeverityFilter.all,
    String? search,
    DateTime? around,
    int aroundMinutes = 10,
    int skip = 0,
    int take = 150,
  }) async {
    entryCalls.add(_EntryCall(severity, search, around, aroundMinutes, skip, take));
    LogEntry entry(String text, LogSeverity sev) =>
        LogEntry(time: DateTime(2026, 11, 9, 14, 20, 11), severity: sev, source: 'disk 153', message: text);
    if (around != null) {
      return LogEntriesPage(total: 2, items: [entry('around one', LogSeverity.error), entry('around two', LogSeverity.warning)]);
    }
    final count = skip == 0 ? 150 : 50;
    return LogEntriesPage(
      total: 200,
      items: [for (var i = 0; i < count; i++) entry('line ${skip + i}', i.isEven ? LogSeverity.warning : LogSeverity.info)],
    );
  }

  @override
  Future<Uint8List> downloadLogs(String hostId, String id) async => Uint8List.fromList([80, 75, 3, 4]);

  final searches = <(String, int)>[];
  Future<List<DeviceSearchResult>> Function(String text)? searchHandler;

  @override
  Future<List<DeviceSearchResult>> search(String text, {int take = 10}) {
    searches.add((text, take));
    return searchHandler!(text);
  }
}

class _Saver implements FileSaver {
  final saved = <(Uint8List, String, String?)>[];

  @override
  Future<void> save(Uint8List bytes, String fileName, {String mimeType = 'application/octet-stream'}) async =>
      saved.add((bytes, fileName, mimeType));
}

class _AutonomyRepo implements AutonomyRepository {
  @override
  Future<Autonomy> get(AutonomyPeriod period, {String? month}) async => _autonomy();

  @override
  Future<AutonomyDevicesPage> devices(String key, {int skip = 0, int take = 25, String? search}) =>
      throw UnimplementedError();

  @override
  Future<AutonomyPatchNowResult> patchNow(String key, {required int expectedConnected, required bool includeOffline}) =>
      throw UnimplementedError();
}

Autonomy _autonomy() => autonomyFromJson({
      'period': '30d',
      'deviceHealth': {
        'unstable': 1,
        'watch': 0,
        'items': [
          {
            'hostId': 'hh1',
            'hostname': 'laptop-ceo',
            'status': 'Unstable',
            'why': '4 unexpected shutdowns in the last 7 days',
            'lastEventUtc': '2026-11-06T04:04:00Z',
          },
        ],
      },
    });

void main() {
  group('the mapper', () {
    final report = deviceReportFromJson(_reportJson(logs: _log('Completed', truncatedBefore: '2026-10-24T00:00:00Z')));

    test('reads the last completed collection beside the newest one, and is happy without it', () {
      final both = deviceReportFromJson({
        ..._reportJson(logs: _log('Failed', id: 'new', failure: 'x')),
        'lastCompletedLogs': _log('Completed', id: 'old'),
      });

      expect(both.logs!.status, LogCollectionStatus.failed);
      expect(both.lastCompletedLogs!.id, 'old');
      expect(both.lastCompletedLogs!.isCompleted, isTrue);
      expect(report.lastCompletedLogs, isNull);   // an older server, or none ever completed
    });

    test('reads the host, health, events, Kintsugi actions, hypotheses and install runs', () {
      expect(report.host.hostname, 'LAPTOP-CEO-01');
      expect(report.host.platform, DevicePlatform.windows);
      expect(report.host.lastSeenUtc, isNotNull);
      expect(report.days, 7);
      expect(report.health.status, DeviceHealthStatus.unstable);
      expect(report.health.fatalErrors30d, 1);
      expect(report.events.map((e) => e.kind), [
        DeviceReportEventKind.hardwareErrorCorrected,
        DeviceReportEventKind.unexpectedShutdown,
        DeviceReportEventKind.unexpectedShutdown,
        DeviceReportEventKind.hardwareErrorFatal,
      ]);
      expect(report.kintsugiActions.map((a) => a.kind), [KintsugiActionKind.install, KintsugiActionKind.osUpdate]);
      expect(report.shutdownsNearKintsugi, 1);
      expect(report.hypotheses.first.confidence, HypothesisConfidence.high);
      expect(report.hypotheses.first.confidence.pips, 3);
      expect(report.hypotheses.last.confidence, HypothesisConfidence.unlikely);
      expect(report.installRuns.last.succeeded, isFalse);
      expect(report.installRuns.last.output, contains('1603'));
    });

    test('a shutdown carries the nearest Kintsugi action, other events have none', () {
      final near = report.events.firstWhere((e) => e.source == 'Kernel-Power 41').kintsugiNearby!;
      expect(near.action, KintsugiActionKind.install);
      expect(near.minutesBefore, 12.0);
      expect(near.within30Minutes, isTrue);
      expect(report.events.first.kintsugiNearby, isNull);
    });

    test('reads the collection with its cap and keep-until', () {
      final logs = report.logs!;
      expect(logs.status, LogCollectionStatus.completed);
      expect(logs.detail, LogDetail.warningsAndErrors);
      expect(logs.entryCount, 4210);
      expect(logs.sizeBytes, 2621440);
      expect(logs.truncatedBeforeUtc, isNotNull);
      expect(logs.requestedBy, 'sara@example.com');
      expect(logs.isOpen, isFalse);
    });

    test('a report with no collection has null logs', () {
      expect(deviceReportFromJson(_reportJson()).logs, isNull);
    });

    test('a null kintsugiNearby on a shutdown is read as none', () {
      final json = _reportJson();
      json['events'] = [
        for (final e in json['events'] as List) <String, dynamic>{...(e as Map<String, dynamic>), 'kintsugiNearby': null},
      ];
      final shutdown = deviceReportFromJson(json).events[1];
      expect(shutdown.kintsugiNearby, isNull);
      expect(shutdown.isShutdown, isTrue);
    });

    test('a 204 (no body, which ApiClient hands back as null) is no collection', () {
      expect(logCollectionOrNullFromJson(null), isNull);
    });

    test('a failed collection keeps its reason; an unknown status reads as failed', () {
      final failed = logCollectionFromJson(_log('Failed', failure: 'The agent is too old to collect logs.'));
      expect(failed.status, LogCollectionStatus.failed);
      expect(failed.failureReason, 'The agent is too old to collect logs.');
      expect(logCollectionStatusFromJson('Something new'), LogCollectionStatus.failed);
    });

    test('reads a page of entries with severities', () {
      final page = logEntriesPageFromJson({
        'total': 3,
        'items': [
          {'t': '2026-11-09T14:20:11Z', 'sev': 'Critical', 'src': 'Kernel-Power 41', 'msg': 'rebooted'},
          {'t': '2026-11-09T14:19:11Z', 'sev': 'Warning', 'src': 'disk 153', 'msg': 'retried'},
        ],
      });
      expect(page.total, 3);
      expect(page.items.map((e) => e.severity), [LogSeverity.critical, LogSeverity.warning]);
      expect(page.items.first.source, 'Kernel-Power 41');
    });
  });

  group('the five log event kinds', () {
    test('map by their server names, in the server order after the health kinds', () {
      expect(hostEventKindFromJson('LogsRequested'), HostEventKind.logsRequested);
      expect(hostEventKindFromJson('LogsCollected'), HostEventKind.logsCollected);
      expect(hostEventKindFromJson('LogsDownloaded'), HostEventKind.logsDownloaded);
      expect(hostEventKindFromJson('LogsDeleted'), HostEventKind.logsDeleted);
      expect(hostEventKindFromJson('LogsFailed'), HostEventKind.logsFailed);
      // The same by ordinal, because the enum is mirrored in declaration order.
      expect(hostEventKindFromJson(9), HostEventKind.logsRequested);
      expect(hostEventKindFromJson(13), HostEventKind.logsFailed);
    });

    test('say what happened and sit under Admin actions', () {
      HostEvent event(HostEventKind kind) => HostEvent(
            id: 'e',
            occurredUtc: DateTime(2026, 11, 9),
            kind: kind,
            subject: 'LAPTOP',
            fromVersion: null,
            toVersion: null,
            actor: null,
            detail: null,
            count: 1,
          );
      expect(event(HostEventKind.logsRequested).summary, 'Logs requested from the device');
      expect(event(HostEventKind.logsCollected).summary, 'The device sent its logs');
      expect(event(HostEventKind.logsDownloaded).summary, 'Logs downloaded');
      expect(event(HostEventKind.logsDeleted).summary, 'Logs deleted');
      expect(event(HostEventKind.logsFailed).summary, 'The device could not send its logs');
      for (final k in [
        HostEventKind.logsRequested,
        HostEventKind.logsCollected,
        HostEventKind.logsDownloaded,
        HostEventKind.logsDeleted,
        HostEventKind.logsFailed,
      ]) {
        expect(k.group, HostEventGroup.adminActions);
      }
    });
  });

  group('the transport and repository', () {
    test('getBytes returns the body untouched and asks for any type', () async {
      String? accept;
      final api = ApiClient(
        httpClient: MockClient((request) async {
          accept = request.headers['Accept'];
          return http.Response.bytes([80, 75, 3, 4, 0xFF], 200, headers: {'content-type': 'application/zip'});
        }),
      );
      expect(await api.getBytes('/api/x/download'), [80, 75, 3, 4, 0xFF]);
      expect(accept, '*/*');
    });

    test('getBytes announces a 401 like every other call and says so', () async {
      var announced = 0;
      final notifier = UnauthorizedNotifier();
      notifier.stream.listen((_) => announced++);
      final api = ApiClient(
        unauthorizedNotifier: notifier,
        httpClient: MockClient((_) async => http.Response('{}', 401, headers: {'content-type': 'application/problem+json'})),
      );
      await expectLater(api.getBytes('/api/x/download'), throwsA(isA<UnauthorizedApiException>()));
      await Future<void>.delayed(Duration.zero);
      expect(announced, 1);
    });

    test('getBytes raises the server message on an error status', () async {
      final api = ApiClient(
        httpClient: MockClient((_) async => http.Response(
              jsonEncode({'detail': 'That collection is not complete.'}),
              409,
              headers: {'content-type': 'application/problem+json'},
            )),
      );
      await expectLater(
        api.getBytes('/api/x/download'),
        throwsA(isA<ApiException>().having((e) => e.message, 'message', 'That collection is not complete.')),
      );
    });

    test('the repository sends the right requests and reads 204 as no collection', () async {
      final seen = <String>[];
      final bodies = <String>[];
      final repo = DeviceReportRepositoryImpl(ApiClient(
        httpClient: MockClient((request) async {
          seen.add('${request.method} ${request.url.path}?${request.url.query}');
          bodies.add(request.body);
          if (request.url.path.endsWith('/logs') && request.method == 'GET') return http.Response('', 204);
          if (request.url.path.endsWith('/logs') && request.method == 'POST') {
            return http.Response(jsonEncode(_log('Requested')), 201, headers: {'content-type': 'application/json'});
          }
          if (request.method == 'DELETE') return http.Response('', 204);
          if (request.url.path.endsWith('/entries')) {
            return http.Response(jsonEncode({'total': 0, 'items': []}), 200, headers: {'content-type': 'application/json'});
          }
          return http.Response(jsonEncode(_reportJson()), 200, headers: {'content-type': 'application/json'});
        }),
      ));

      await repo.get('h1', days: 30);
      expect(await repo.latestLogs('h1'), isNull);
      final created = await repo.requestLogs('h1', periodDays: 7, detail: LogDetail.everything);
      await repo.deleteLogs('h1', 'c1');
      await repo.entries(
        'h1',
        'c1',
        severity: LogSeverityFilter.errors,
        search: 'disk',
        around: DateTime.utc(2026, 11, 9, 14, 20),
        skip: 150,
      );

      expect(created.status, LogCollectionStatus.requested);
      expect(seen[0], 'GET /api/admin/device-report/h1?days=30');
      expect(seen[1], 'GET /api/admin/device-report/h1/logs?');
      expect(jsonDecode(bodies[2]), {'periodDays': 7, 'detail': 'Everything'});
      expect(seen[3], 'DELETE /api/admin/device-report/h1/logs/c1?');
      expect(
        seen[4],
        'GET /api/admin/device-report/h1/logs/c1/entries?severity=errors&search=disk&aroundUtc=2026-11-09T14%3A20%3A00.000Z&aroundMinutes=10&skip=150&take=150',
      );
    });
  });

  group('the device search', () {
    test('the mapper reads results, and a bare empty array is no results', () {
      final results = deviceSearchResultsFromJson([
        {'hostId': 'h1', 'hostname': 'LAPTOP-01', 'operatingSystem': 'Windows 11', 'platform': 'Windows'},
        {'hostId': 'h2', 'hostname': 'mac-2', 'operatingSystem': null, 'platform': 'macOS'},
      ]);
      expect(results.map((r) => r.hostname), ['LAPTOP-01', 'mac-2']);
      expect(results.last.platform, DevicePlatform.macOS);
      expect(results.last.operatingSystem, isNull);
      expect(results.last.status, DeviceHealthStatus.healthy, reason: 'a missing status is healthy');
      expect(results.last.why, '');
      expect(results.last.lastEventUtc, isNull);
      expect(deviceSearchResultsFromJson([]), isEmpty);

      final flagged = deviceSearchResultFromJson({
        'hostId': 'h3',
        'hostname': 'B',
        'platform': 'Windows',
        'status': 'Unstable',
        'why': '3 shutdowns',
        'lastEventUtc': '2026-11-06T04:04:00Z',
      });
      expect(flagged.status, DeviceHealthStatus.unstable);
      expect(flagged.why, '3 shutdowns');
      expect(flagged.lastEventUtc!.toUtc(), DateTime.utc(2026, 11, 6, 4, 4));
    });

    test('the repository sends q and take', () async {
      Uri? seen;
      final repo = DeviceReportRepositoryImpl(ApiClient(
        httpClient: MockClient((request) async {
          seen = request.url;
          return http.Response(jsonEncode([{'hostId': 'h1', 'hostname': 'A', 'platform': 'Linux'}]), 200,
              headers: {'content-type': 'application/json'});
        }),
      ));
      final found = await repo.search('ab c', take: 10);
      expect(seen!.path, '/api/admin/device-report/search');
      expect(seen!.queryParameters, {'q': 'ab c', 'take': '10'});
      expect(found.single.platform, DevicePlatform.linux);
    });
  });

  group('the PDF model', () {
    final report = deviceReportFromJson(_reportJson(logs: _log('Completed')));
    final now = DateTime.now();

    String words(ReportDocument d) => [
          d.title,
          d.subtitle,
          for (final s in d.sections) ...[
            s.title,
            for (final b in s.blocks)
              switch (b) {
                ReportText() => b.text,
                ReportBullets() => b.items.join(' '),
                ReportStats() => b.items.map((i) => '${i.$1} ${i.$2}').join(' '),
                ReportTable() => [b.headers.join(' '), ...b.rows.map((r) => r.join(' '))].join(' '),
                ReportColumns() => '${b.title} ${b.ownLabel} ${b.adminLabel}',
                ReportBars() => b.title,
                ReportLine() => b.title,
              },
          ],
        ].join('\n');

    test('has the sections that were asked for, and only those', () {
      final all = buildDeviceReport(
        report,
        const DeviceReportOptions(installOutput: true),
        now,
        logGroups: [
          LogLinesGroup(
            around: null,
            lines: [LogEntry(time: DateTime(2026, 11, 9, 14), severity: LogSeverity.error, source: 'volmgr 161', message: 'Dump file creation failed')],
          ),
        ],
      );
      expect(all.sections.map((s) => s.title), [
        'What happened',
        'Timeline',
        'Was Kintsugi involved?',
        'What it could be',
        'Install output',
        'Log lines',
      ]);
      final text = words(all);
      expect(text, contains('LAPTOP-CEO-01'));
      expect(text, contains('shut down by itself 2 times in the last 7 days'));
      expect(text, contains('Possibly'));
      expect(text, contains('Not involved'));
      expect(text, contains('Hardware fault in the processor or memory (high confidence)'));
      expect(text, contains('ERROR 1603'));
      expect(text, contains('Dump file creation failed'));

      final none = buildDeviceReport(
        report,
        const DeviceReportOptions(summary: false, timeline: false, involvement: false, hypotheses: false, logLines: false),
        now,
      );
      expect(none.sections, isEmpty);
    });

    test('the timeline chart counts shutdowns and hardware errors per day with its own legend', () {
      final timeline = buildDeviceReport(report, const DeviceReportOptions(), now).sections.firstWhere((s) => s.title == 'Timeline');
      final chart = timeline.blocks.whereType<ReportColumns>().single;
      expect(chart.bars, hasLength(7));
      expect(chart.bars.fold<int>(0, (n, b) => n + b.$2), 2);
      expect(chart.ownLabel, 'unexpected shutdowns');
    });

    test('is written as a PDF in the browser, with the device in it', () async {
      final bytes = await reportToPdf(buildDeviceReport(report, const DeviceReportOptions(), now));
      expect(ascii.decode(bytes.sublist(0, 5)), '%PDF-');
      final out = <String>[];
      for (final m in RegExp(r'stream\r?\n(.*?)\r?\nendstream', dotAll: true).allMatches(latin1.decode(bytes))) {
        try {
          final inflated = latin1.decode(zlib.decode(latin1.encode(m.group(1)!)));
          out.addAll(RegExp(r'\[\((.*?)\)\]TJ').allMatches(inflated).map((x) => x.group(1)!));
        } on Object {
          // Not a compressed text stream.
        }
      }
      expect(out.join(' '), contains('LAPTOP-CEO-01'));
    });
  });

  group('the screen', () {
    late _Repo repo;
    late _Saver saver;

    setUp(() {
      repo = _Repo(_reportJson());
      saver = _Saver();
    });
    tearDown(() => locator.reset());

    Future<void> pump(WidgetTester tester, {bool settle = true}) async {
      locator
        ..registerSingleton(GetDeviceReport(repo))
        ..registerSingleton(GetLatestLogCollection(repo))
        ..registerSingleton(RequestLogCollection(repo))
        ..registerSingleton(DeleteLogCollection(repo))
        ..registerSingleton(GetLogEntries(repo))
        ..registerSingleton(DownloadLogCollection(repo))
        ..registerSingleton<FileSaver>(saver);
      final router = GoRouter(
        initialLocation: Routes.deviceReport('h1'),
        routes: [
          GoRoute(path: Routes.autonomy, builder: (_, _) => const Text('AUTONOMY PAGE')),
          GoRoute(
            path: '/autonomy/device/:hostId',
            builder: (_, state) => Scaffold(body: DeviceReportScreen(hostId: state.pathParameters['hostId']!)),
          ),
        ],
      );
      tester.view.physicalSize = const Size(1500, 6000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp.router(theme: AppTheme.light(), routerConfig: router));
      if (settle) {
        await tester.pumpAndSettle();
      } else {
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 50));
      }
    }

    /// Takes the page down, so a poll timer cannot outlive the test.
    Future<void> unmount(WidgetTester tester) => tester.pumpWidget(const SizedBox.shrink());

    testWidgets('names the device, its health and why, and says what happened', (tester) async {
      await pump(tester);

      expect(find.text('LAPTOP-CEO-01'), findsWidgets);
      expect(find.widgetWithText(StatusChip, 'UNSTABLE'), findsOneWidget);
      expect(find.text('7XK2M93'), findsOneWidget);
      expect(find.text('Windows 11 Pro 23H2'), findsOneWidget);
      expect(find.text('0.12.6'), findsOneWidget);
      expect(find.textContaining('Why it is flagged: 2 unexpected shutdowns', findRichText: true), findsOneWidget);
      expect(
        find.textContaining(
          'LAPTOP-CEO-01 shut down by itself 2 times in the last 7 days (2 in 30 days). It also logged 1 fatal and 1 corrected hardware error. '
          'Kintsugi was close to 1 of the 2 shutdowns',
          findRichText: true,
        ),
        findsOneWidget,
      );
      expect(find.textContaining('Most likely: Hardware fault in the processor or memory (high confidence).'), findsOneWidget);
      expect(find.textContaining('not a diagnosis'), findsWidgets);
    });

    testWidgets('lists every event with its source and what it said, and Kintsugi close by', (tester) async {
      await pump(tester);

      expect(find.text('Kernel-Power 41'), findsOneWidget);
      expect(find.text('WHEA-Logger 18'), findsOneWidget);
      expect(find.text('A corrected hardware error occurred. Component: Memory'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, 'UNEXPECTED SHUTDOWN'), findsNWidgets(2));
      expect(find.widgetWithText(StatusChip, 'CORRECTED HARDWARE ERROR'), findsOneWidget);
      // The table's own column: one shutdown has Kintsugi close by, the other has not.
      expect(find.widgetWithText(StatusChip, 'NO'), findsOneWidget);
    });

    testWidgets('says Possibly only for a shutdown with an action within 30 minutes, and Not involved otherwise', (tester) async {
      await pump(tester);

      expect(find.widgetWithText(StatusChip, 'POSSIBLY'), findsNWidgets(2), reason: 'the table and the involvement list');
      expect(find.widgetWithText(StatusChip, 'NOT INVOLVED'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, '1 of 2 possibly involved'.toUpperCase()), findsOneWidget);
      expect(find.text('Zoom 6.2.5 (install), 12 min before.'), findsOneWidget);
      expect(
        find.text('Nearest Kintsugi action: Google Chrome 130.0 (install), 4 h 30 min before. Too far to be the cause.'),
        findsOneWidget,
      );
    });

    testWidgets('shows each hypothesis with its confidence, evidence and what to check next, as the server wrote them', (tester) async {
      await pump(tester);

      expect(find.text('Hardware fault in the processor or memory'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, 'HIGH CONFIDENCE'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, 'UNLIKELY'), findsOneWidget);
      expect(find.text('•  1 fatal hardware error (WHEA-Logger 18) in 30 days.'), findsOneWidget);
      expect(find.text("•  Run the maker's diagnostics and a memory test."), findsOneWidget);
      expect(find.text('Ranked from the evidence below. A hint to start the checking, not a diagnosis.'), findsOneWidget);
    });

    testWidgets('lists the install runs, and a failed one expands to its output', (tester) async {
      await pump(tester);

      expect(find.widgetWithText(StatusChip, 'INSTALLED'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, 'FAILED'), findsOneWidget);
      expect(find.text('ERROR 1603: Fatal error during installation.'), findsNothing);

      await tester.tap(find.text('Output'));
      await tester.pumpAndSettle();
      expect(find.text('ERROR 1603: Fatal error during installation.'), findsOneWidget);

      await tester.tap(find.text('Hide output'));
      await tester.pumpAndSettle();
      expect(find.text('ERROR 1603: Fatal error during installation.'), findsNothing);
    });

    testWidgets('the timeline legend uses the corrected Kintsugi wording', (tester) async {
      await pump(tester);

      expect(find.text('Kintsugi installed an update'), findsOneWidget);
      expect(find.text('Kintsugi installed a system update (can restart the device)'), findsOneWidget);
      expect(find.text('Kintsugi restarted the device'), findsNothing);
    });

    testWidgets('a period button reads the report again for that many days', (tester) async {
      await pump(tester);
      expect(repo.getDays, [7]);

      await tester.tap(find.text('LAST 30 DAYS'));
      await tester.pumpAndSettle();

      expect(repo.getDays, [7, 30]);
    });

    testWidgets('the back link goes to Autonomy', (tester) async {
      await pump(tester);

      await tester.tap(find.text('Autonomy'));
      await tester.pumpAndSettle();

      expect(find.text('AUTONOMY PAGE'), findsOneWidget);
    });

    testWidgets('clicking a mark on the timeline selects that event', (tester) async {
      await pump(tester);
      final event = deviceReportFromJson(repo.json).events.firstWhere((e) => e.source == 'EventLog 6008');

      await tester.tap(find.byKey(ValueKey('mark-${event.occurredUtc.microsecondsSinceEpoch}-${event.kind.name}')));
      await tester.pumpAndSettle();

      expect(find.text('Collect logs to read what the device wrote around this time.'), findsWidgets);
    });

    group('the logs panel', () {
      testWidgets('with nothing collected explains it and offers to collect, with the hourly check-in', (tester) async {
        await pump(tester);

        expect(find.textContaining('Nothing has been collected from this device.'), findsOneWidget);
        expect(find.textContaining('about once an hour'), findsOneWidget);
        expect(find.textContaining('within 15 minutes'), findsNothing);
        expect(find.text('COLLECT LOGS NOW'), findsNWidgets(2));
      });

      testWidgets('requested shows who asked and the wait, and cancelling deletes the request', (tester) async {
        repo.setLogs(_log('Requested'));
        await pump(tester);

        expect(find.textContaining('Requested'), findsWidgets);
        expect(find.textContaining('by sara@example.com. Waiting for the device to check in (last seen 2 min ago).'), findsOneWidget);
        expect(find.textContaining('about once an hour, and the request waits up to 24 hours, then shows as failed'), findsOneWidget);

        await tester.tap(find.text('CANCEL REQUEST'));
        await tester.pumpAndSettle();

        expect(repo.deleted, ['c1']);
        expect(find.textContaining('Nothing has been collected from this device.'), findsOneWidget);
        expect(find.text('Request cancelled.'), findsOneWidget);
        await unmount(tester);
      });

      testWidgets('polls every 5 seconds while requested, and reads the whole report again once it is completed', (tester) async {
        repo.setLogs(_log('Requested'));
        await pump(tester);
        expect(repo.getDays, [7]);

        await tester.pump(const Duration(seconds: 4));
        await tester.pump();
        expect(repo.getDays, [7], reason: 'not yet');

        repo.setLogs(_log('Collecting'));
        await tester.pump(const Duration(seconds: 1));
        await tester.pump();
        await tester.pump();
        expect(find.textContaining('The device is reading its log…'), findsOneWidget);
        expect(repo.getDays, [7], reason: 'still waiting, so only the panel changed');

        repo.setLogs(_log('Completed'));
        await tester.pump(const Duration(seconds: 5));
        await tester.pump();
        await tester.pump();
        await tester.pumpAndSettle();

        expect(repo.getDays, [7, 7], reason: 'the report was read again');
        expect(find.textContaining('4,210 entries'), findsOneWidget);

        // And it has stopped: nothing more is read.
        await tester.pump(const Duration(seconds: 20));
        expect(repo.getDays, [7, 7]);
      });

      testWidgets('stops polling when the page goes away', (tester) async {
        repo.setLogs(_log('Collecting'));
        await pump(tester, settle: false);
        await unmount(tester);

        // A timer left running would make this fail with "a Timer is still pending".
        await tester.pump(const Duration(seconds: 10));
      });

      testWidgets('failed shows the reason and offers to try again', (tester) async {
        repo.setLogs(_log('Failed', failure: 'The device did not check in within 24 hours, so the request was dropped.'));
        await pump(tester);

        expect(find.text('The device did not check in within 24 hours, so the request was dropped.'), findsOneWidget);
        expect(find.text('TRY AGAIN'), findsOneWidget);
        expect(repo.entryCalls, isEmpty);
      });

      testWidgets('completed summarises the collection, with the cap note when it was cut', (tester) async {
        repo.setLogs(_log('Completed', truncatedBefore: '2026-10-24T08:00:00Z'));
        await pump(tester);

        expect(find.textContaining('last 30 days · warnings and errors · 4,210 entries · 2.5 MB compressed · kept until', findRichText: true), findsOneWidget);
        expect(find.textContaining('Cut at the 5 MB cap: nothing before Oct 24'), findsOneWidget);
        expect(find.text('200 entries'), findsOneWidget);
        expect(find.text('line 0'), findsOneWidget);
      });

      testWidgets('completed without a cap says nothing about one', (tester) async {
        repo.setLogs(_log('Completed'));
        await pump(tester);

        expect(find.textContaining('Cut at the 5 MB cap'), findsNothing);
      });

      testWidgets('the severity buttons ask the server, and the first page is 150', (tester) async {
        repo.setLogs(_log('Completed'));
        await pump(tester);
        expect(repo.entryCalls.single.severity, LogSeverityFilter.all);
        expect(repo.entryCalls.single.take, 150);

        await tester.tap(find.text('ERRORS'));
        await tester.pumpAndSettle();
        expect(repo.entryCalls.last.severity, LogSeverityFilter.errors);
        expect(repo.entryCalls.last.skip, 0);

        await tester.tap(find.text('WARNINGS'));
        await tester.pumpAndSettle();
        expect(repo.entryCalls.last.severity, LogSeverityFilter.warnings);
      });

      testWidgets('search waits 400 ms after the last key, then asks the server', (tester) async {
        repo.setLogs(_log('Completed'));
        await pump(tester);
        final before = repo.entryCalls.length;

        await tester.enterText(find.byType(TextField), 'disk');
        await tester.pump(const Duration(milliseconds: 399));
        expect(repo.entryCalls.length, before, reason: 'still debouncing');

        await tester.enterText(find.byType(TextField), 'disk retry');
        await tester.pump(const Duration(milliseconds: 399));
        expect(repo.entryCalls.length, before, reason: 'another key restarts the wait');

        await tester.pump(const Duration(milliseconds: 5));
        await tester.pumpAndSettle();
        expect(repo.entryCalls.length, before + 1);
        expect(repo.entryCalls.last.search, 'disk retry');
      });

      testWidgets('"Show 150 more" reads the next page with skip', (tester) async {
        repo.setLogs(_log('Completed'));
        await pump(tester);
        expect(find.text('line 149'), findsOneWidget);
        expect(find.text('line 150'), findsNothing);

        await tester.ensureVisible(find.text('Show 150 more'));
        await tester.tap(find.text('Show 150 more'));
        await tester.pumpAndSettle();

        expect(repo.entryCalls.last.skip, 150);
        expect(find.text('line 150'), findsOneWidget);
        expect(find.text('line 0'), findsOneWidget, reason: 'the first page stays');
        expect(find.text('Show 150 more'), findsNothing, reason: 'all 200 are shown');
      });

      testWidgets('selecting an event narrows the viewer to 10 minutes around it, with a way back to everything', (tester) async {
        repo.setLogs(_log('Completed'));
        await pump(tester);
        expect(repo.entryCalls.last.around, isNull);

        await tester.tap(find.text('EventLog 6008'));
        await tester.pumpAndSettle();

        expect(repo.entryCalls.last.around, isNotNull);
        expect(repo.entryCalls.last.aroundMinutes, 10);
        expect(find.textContaining('Showing 10 minutes before and after'), findsOneWidget);
        expect(find.text('around one'), findsOneWidget);
        expect(find.text('2 entries'), findsOneWidget);

        await tester.tap(find.text('Show everything'));
        await tester.pumpAndSettle();

        expect(repo.entryCalls.last.around, isNull);
        expect(find.textContaining('Showing 10 minutes before and after'), findsNothing);
        expect(find.text('line 0'), findsOneWidget);
      });

      testWidgets('Download hands the zip to the file saver', (tester) async {
        repo.setLogs(_log('Completed'));
        await pump(tester);

        await tester.tap(find.text('DOWNLOAD (.ZIP)'));
        await tester.pumpAndSettle();

        expect(saver.saved.single.$2, 'LAPTOP-CEO-01-logs.zip');
        expect(saver.saved.single.$3, 'application/zip');
        expect(saver.saved.single.$1, [80, 75, 3, 4]);
      });

      // ---- a newer request must not hide the last collected log (review finding 2) ----

      testWidgets('a failed new request keeps the last collected log underneath, and the guesses say which log they read', (tester) async {
        repo.setNewestAndLastCompleted(
          _log('Failed', id: 'new', failure: 'The device did not check in within 24 hours.'),
          _log('Completed', id: 'old', days: 7),
        );
        await pump(tester);

        expect(find.text('The device did not check in within 24 hours.'), findsOneWidget);
        expect(find.text('TRY AGAIN'), findsOneWidget);
        expect(find.text('LAST COLLECTED LOG'), findsOneWidget);
        expect(find.textContaining('This is the log from before the request above.'), findsOneWidget);
        expect(find.text('line 0'), findsOneWidget);
        expect(find.textContaining('last 7 days · warnings and errors · 4,210 entries', findRichText: true), findsOneWidget);
        expect(repo.entryCalls, isNotEmpty);
        expect(find.textContaining('Evidence includes the log collected'), findsOneWidget);
        expect(find.textContaining('(last 7 days). A newer request failed.'), findsOneWidget);
      });

      testWidgets('a waiting new request keeps the last collected log underneath, with its own Cancel request', (tester) async {
        repo.setNewestAndLastCompleted(_log('Requested', id: 'new'), _log('Completed', id: 'old'));
        await pump(tester);

        expect(find.textContaining('Waiting for the device to check in'), findsOneWidget);
        expect(find.text('CANCEL REQUEST'), findsOneWidget);
        expect(find.text('LAST COLLECTED LOG'), findsOneWidget);
        expect(find.text('DELETE NOW'), findsOneWidget);
        expect(find.textContaining('A newer request has not finished.'), findsOneWidget);
        await unmount(tester);
      });

      testWidgets('deleting the kept log leaves the newest request and takes the note away', (tester) async {
        repo.setNewestAndLastCompleted(
          _log('Failed', id: 'new', failure: 'The device did not check in within 24 hours.'),
          _log('Completed', id: 'old'),
        );
        await pump(tester);

        await tester.tap(find.text('DELETE NOW'));
        await tester.pumpAndSettle();
        await tester.tap(find.widgetWithText(PrimaryButton, 'DELETE'));
        await tester.pumpAndSettle();

        expect(repo.deleted, ['old']);
        expect(find.text('The device did not check in within 24 hours.'), findsOneWidget);
        expect(find.text('LAST COLLECTED LOG'), findsNothing);
        expect(find.textContaining('Evidence includes the log collected'), findsNothing);
        expect(find.text('Collection deleted.'), findsOneWidget);
      });

      testWidgets('when the newest collection is itself completed there is no second log and no note', (tester) async {
        repo.setNewestAndLastCompleted(_log('Completed', id: 'new'), _log('Completed', id: 'new'));
        await pump(tester);

        expect(find.text('LAST COLLECTED LOG'), findsNothing);
        expect(find.textContaining('Evidence includes the log collected'), findsNothing);
        expect(find.text('DELETE NOW'), findsOneWidget);
      });

      testWidgets('the PDF dialog still offers the log lines while a newer request waits', (tester) async {
        repo.setNewestAndLastCompleted(_log('Requested', id: 'new'), _log('Completed', id: 'old'));
        await pump(tester);

        await tester.tap(find.text('DOWNLOAD REPORT (PDF)'));
        await tester.pumpAndSettle();

        expect(find.text('Collect logs first.'), findsNothing);
        expect(find.textContaining('Log lines around the selected events'), findsOneWidget);
        await unmount(tester);
      });

      testWidgets('Delete now asks first, then deletes and goes back to nothing collected', (tester) async {
        repo.setLogs(_log('Completed'));
        await pump(tester);

        await tester.tap(find.text('DELETE NOW'));
        await tester.pumpAndSettle();
        expect(find.text('Delete this collection now?'), findsOneWidget);

        await tester.tap(find.text('CANCEL'));
        await tester.pumpAndSettle();
        expect(repo.deleted, isEmpty);

        await tester.tap(find.text('DELETE NOW'));
        await tester.pumpAndSettle();
        await tester.tap(find.widgetWithText(PrimaryButton, 'DELETE'));
        await tester.pumpAndSettle();

        expect(repo.deleted, ['c1']);
        expect(find.textContaining('Nothing has been collected from this device.'), findsOneWidget);
        expect(find.text('Collection deleted.'), findsOneWidget);
      });
    });

    group('the Collect dialog', () {
      testWidgets('posts the chosen period and detail, then shows the request as waiting', (tester) async {
        await pump(tester);

        await tester.tap(find.text('COLLECT LOGS NOW').first);
        await tester.pumpAndSettle();

        expect(find.text('Collect logs from LAPTOP-CEO-01'), findsOneWidget);
        expect(find.textContaining('The Windows System and Application event logs'), findsOneWidget);
        expect(find.textContaining('Stops at 5 MB compressed and keeps the newest part. Kept on the server for 30 days.'), findsOneWidget);
        expect(find.text('Everything can include user names and file paths.'), findsNothing);

        final dialog = find.byType(AlertDialog);
        await tester.tap(find.descendant(of: dialog, matching: find.text('LAST 7 DAYS')));
        await tester.tap(find.descendant(of: dialog, matching: find.text('EVERYTHING')));
        await tester.pumpAndSettle();
        expect(find.text('Everything can include user names and file paths.'), findsOneWidget);

        await tester.tap(find.text('ASK THE DEVICE'));
        await tester.pumpAndSettle();

        expect(repo.requested, [(7, LogDetail.everything)]);
        expect(find.textContaining('Waiting for the device to check in'), findsOneWidget);
        expect(find.text('Requested. The device will be asked at its next check-in.'), findsOneWidget);
        await unmount(tester);
      });

      testWidgets('defaults to the last 30 days of warnings and errors', (tester) async {
        await pump(tester);

        await tester.tap(find.text('COLLECT LOGS NOW').first);
        await tester.pumpAndSettle();
        await tester.tap(find.text('ASK THE DEVICE'));
        await tester.pumpAndSettle();

        expect(repo.requested, [(30, LogDetail.warningsAndErrors)]);
        await unmount(tester);
      });

      testWidgets("shows the server's message when a collection is already open, and stays open", (tester) async {
        repo.requestError = const ApiException('A log collection is already waiting on this device.', statusCode: 409);
        await pump(tester);

        await tester.tap(find.text('COLLECT LOGS NOW').first);
        await tester.pumpAndSettle();
        await tester.tap(find.text('ASK THE DEVICE'));
        await tester.pumpAndSettle();

        expect(find.text('A log collection is already waiting on this device.'), findsOneWidget);
        expect(find.byType(AlertDialog), findsOneWidget);
        expect(repo.requested, isEmpty);
      });
    });

    group('the PDF dialog', () {
      testWidgets('offers the log lines only once logs are collected, and never the whole log', (tester) async {
        await pump(tester);

        await tester.tap(find.text('DOWNLOAD REPORT (PDF)'));
        await tester.pumpAndSettle();

        expect(find.text('Collect logs first.'), findsOneWidget);
        expect(find.textContaining('whole collected log'), findsNothing);
        expect(find.text('What happened'), findsWidgets);
        expect(find.text('Timeline chart'), findsOneWidget);
        expect(find.text('Install output'), findsWidgets);
      });

      testWidgets('with logs collected the log lines are offered', (tester) async {
        repo.setLogs(_log('Completed'));
        await pump(tester);

        await tester.tap(find.text('DOWNLOAD REPORT (PDF)'));
        await tester.pumpAndSettle();

        expect(find.text('Collect logs first.'), findsNothing);
        expect(find.textContaining('Log lines around the selected events (errors only, up to 200 lines)'), findsOneWidget);
      });

      testWidgets('generating saves kintsugi-<host>-report-<date>.pdf and says nothing was sent anywhere', (tester) async {
        repo.setLogs(_log('Completed'));
        await pump(tester);

        await tester.tap(find.text('DOWNLOAD REPORT (PDF)'));
        await tester.pumpAndSettle();
        await tester.runAsync(() async {
          await tester.tap(find.text('GENERATE PDF'));
          await Future<void>.delayed(const Duration(seconds: 2));
        });
        await tester.pumpAndSettle();

        expect(saver.saved, hasLength(1));
        expect(saver.saved.single.$2, matches(RegExp(r'^kintsugi-LAPTOP-CEO-01-report-\d{4}-\d{2}-\d{2}\.pdf$')));
        expect(saver.saved.single.$3, 'application/pdf');
        expect(ascii.decode(saver.saved.single.$1.sublist(0, 5)), '%PDF-');
        expect(find.textContaining('nothing was sent anywhere'), findsOneWidget);
        final call = repo.entryCalls.last;
        expect(call.severity, LogSeverityFilter.errors);
        expect(call.take, 200);
      });
    });
  });

  group('the Autonomy entry point', () {
    tearDown(() => locator.reset());

    test('the route is /autonomy/device/<id>, with the id encoded', () {
      expect(Routes.deviceReport('hh1'), '/autonomy/device/hh1');
      expect(Routes.deviceReport('a b'), '/autonomy/device/a%20b');
    });

    testWidgets("a device row's Open report goes to that device's report", (tester) async {
      locator.registerSingleton(GetAutonomy(_AutonomyRepo()));
      final router = GoRouter(
        initialLocation: Routes.autonomy,
        routes: [
          GoRoute(path: Routes.autonomy, builder: (_, _) => const Scaffold(body: AutonomyScreen())),
          GoRoute(path: '/autonomy/device/:hostId', builder: (_, state) => Text('REPORT ${state.pathParameters['hostId']}')),
        ],
      );
      tester.view.physicalSize = const Size(1500, 3400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp.router(theme: AppTheme.light(), routerConfig: router));
      await tester.pumpAndSettle();

      expect(find.text('laptop-ceo'), findsOneWidget);
      await tester.tap(find.text('Open report'));
      await tester.pumpAndSettle();

      expect(find.text('REPORT hh1'), findsOneWidget);
    });
  });

  group('Find a device on the Autonomy page', () {
    late _Repo repo;

    setUp(() => repo = _Repo(_reportJson()));
    tearDown(() => locator.reset());

    DeviceSearchResult hit(String id, String name) =>
        DeviceSearchResult(hostId: id, hostname: name, operatingSystem: 'Windows 11', platform: DevicePlatform.windows);

    Future<void> pump(WidgetTester tester) async {
      locator
        ..registerSingleton(GetAutonomy(_AutonomyRepo()))
        ..registerSingleton(SearchDevices(repo));
      final router = GoRouter(
        initialLocation: Routes.autonomy,
        routes: [
          GoRoute(path: Routes.autonomy, builder: (_, _) => const Scaffold(body: AutonomyScreen())),
          GoRoute(path: '/autonomy/device/:hostId', builder: (_, state) => Text('REPORT ${state.pathParameters['hostId']}')),
        ],
      );
      tester.view.physicalSize = const Size(1500, 3400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp.router(theme: AppTheme.light(), routerConfig: router));
      await tester.pumpAndSettle();
    }

    Future<void> type(WidgetTester tester, String text) async {
      await tester.enterText(find.byType(TextField).first, text);
    }

    testWidgets('waits 300 ms, asks nothing under two characters, and shows nothing for them', (tester) async {
      repo.searchHandler = (_) async => [hit('x1', 'PC-X1')];
      await pump(tester);
      expect(find.text('FIND A DEVICE'), findsOneWidget);

      await type(tester, 'p');
      await tester.pump(const Duration(seconds: 1));
      expect(repo.searches, isEmpty);

      await type(tester, 'pc');
      await tester.pump(const Duration(milliseconds: 299));
      expect(repo.searches, isEmpty);
      await tester.pump(const Duration(milliseconds: 2));
      await tester.pump();

      expect(repo.searches, [('pc', 10)]);
      expect(find.text('PC-X1'), findsOneWidget);
    });

    testWidgets('a later key restarts the wait, so only the last text is asked', (tester) async {
      repo.searchHandler = (_) async => [];
      await pump(tester);

      await type(tester, 'pc');
      await tester.pump(const Duration(milliseconds: 200));
      await type(tester, 'pc-1');
      await tester.pump(const Duration(milliseconds: 200));
      expect(repo.searches, isEmpty);
      await tester.pump(const Duration(milliseconds: 150));
      await tester.pump();

      expect(repo.searches, [('pc-1', 10)]);
    });

    testWidgets('a slower earlier answer does not overwrite a newer one', (tester) async {
      final slow = Completer<List<DeviceSearchResult>>();
      repo.searchHandler = (text) => text == 'pc' ? slow.future : Future.value([hit('n1', 'NEWER')]);
      await pump(tester);

      await type(tester, 'pc');
      await tester.pump(DeviceSearch.delay + const Duration(milliseconds: 5));
      await type(tester, 'pcx');
      await tester.pump(DeviceSearch.delay + const Duration(milliseconds: 5));
      await tester.pump();
      expect(find.text('NEWER'), findsOneWidget);

      slow.complete([hit('o1', 'OLDER')]);
      await tester.pump();
      await tester.pump();

      expect(find.text('OLDER'), findsNothing);
      expect(find.text('NEWER'), findsOneWidget);
    });

    testWidgets('clearing the text clears the results and drops an answer still on its way', (tester) async {
      final pending = Completer<List<DeviceSearchResult>>();
      repo.searchHandler = (_) => pending.future;
      await pump(tester);

      await type(tester, 'pc');
      await tester.pump(DeviceSearch.delay + const Duration(milliseconds: 5));
      await type(tester, '');
      pending.complete([hit('a', 'LATE')]);
      await tester.pump();
      await tester.pump();

      expect(find.text('LATE'), findsNothing);
      expect(find.text('No device matches'), findsNothing);
    });

    testWidgets('a result is drawn like the flagged rows: name, reason, last date, status chip and Open report', (tester) async {
      repo.searchHandler = (_) async => [
            DeviceSearchResult(
              hostId: 'x1',
              hostname: 'PC-X1',
              operatingSystem: 'Windows 11',
              platform: DevicePlatform.windows,
              status: DeviceHealthStatus.unstable,
              why: '5 unexpected shutdowns in the last 7 days',
              lastEventUtc: DateTime(2026, 10, 20, 4, 4),
            ),
            DeviceSearchResult(
              hostId: 'x2',
              hostname: 'PC-X2',
              operatingSystem: 'Windows 11',
              platform: DevicePlatform.windows,
              status: DeviceHealthStatus.watch,
              why: '1 unexpected shutdown in 30 days',
              lastEventUtc: DateTime(2026, 11, 3, 9, 12),
            ),
            hit('x3', 'PC-X3'),
          ];
      await pump(tester);

      await type(tester, 'pc-x');
      await tester.pump(DeviceSearch.delay + const Duration(milliseconds: 5));
      await tester.pump();

      expect(find.text('SEARCH RESULTS'), findsOneWidget);
      expect(find.byType(DeviceHealthRowCard), findsNWidgets(4), reason: 'three results and the one flagged row');
      expect(find.text('5 unexpected shutdowns in the last 7 days'), findsOneWidget);
      expect(find.text('last 2026-10-20 04:04'), findsOneWidget);
      expect(find.text('last 2026-11-03 09:12'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, 'UNSTABLE'), findsNWidgets(2), reason: 'the result and the flagged row');
      expect(find.widgetWithText(StatusChip, 'WATCH'), findsOneWidget);
      expect(find.widgetWithText(StatusChip, 'HEALTHY'), findsOneWidget);
      expect(find.text('Windows 11, no unexpected shutdowns or hardware errors in 30 days'), findsOneWidget);
      expect(find.textContaining('last 20'), findsNWidgets(3), reason: 'two results and the flagged row; the healthy result has no date');
    });

    testWidgets('Open report on a result goes to that device', (tester) async {
      repo.searchHandler = (_) async => [hit('x1', 'PC-X1'), hit('x2', 'PC-X2')];
      await pump(tester);

      await type(tester, 'pc-x');
      await tester.pump(DeviceSearch.delay + const Duration(milliseconds: 5));
      await tester.pump();

      // The flagged row comes after the two results.
      await tester.tap(find.text('Open report').at(1));
      await tester.pumpAndSettle();

      expect(find.text('REPORT x2'), findsOneWidget);
    });

    testWidgets('says so when nothing matches', (tester) async {
      repo.searchHandler = (_) async => [];
      await pump(tester);

      await type(tester, 'zzz');
      await tester.pump(DeviceSearch.delay + const Duration(milliseconds: 5));
      await tester.pump();

      expect(find.text('No device matches'), findsOneWidget);
    });

    testWidgets('says so briefly when the search fails', (tester) async {
      repo.searchHandler = (_) async => throw const ApiException('Server is down.');
      await pump(tester);

      await type(tester, 'pc');
      await tester.pump(DeviceSearch.delay + const Duration(milliseconds: 5));
      await tester.pump();

      expect(find.text('Could not search: Server is down.'), findsOneWidget);
      expect(find.text('No device matches'), findsNothing);
    });
  });
}
