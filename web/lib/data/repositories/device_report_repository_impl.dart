import 'dart:typed_data';

import '../../core/network/api_client.dart';
import '../../domain/entities/device_report.dart';
import '../../domain/repositories/repositories.dart';
import '../models/device_report_mapper.dart';

/// Talks to `/api/admin/device-report`.
class DeviceReportRepositoryImpl implements DeviceReportRepository {
  const DeviceReportRepositoryImpl(this._api);

  final ApiClient _api;

  static const _base = '/api/admin/device-report';

  static String _host(String hostId) => '$_base/${Uri.encodeComponent(hostId)}';

  @override
  Future<DeviceReport> get(String hostId, {int days = 7}) async {
    final json = await _api.getJson(_host(hostId), query: {'days': '$days'});
    return deviceReportFromJson(json as Map<String, dynamic>);
  }

  @override
  Future<LogCollection?> latestLogs(String hostId) async =>
      logCollectionOrNullFromJson(await _api.getJson('${_host(hostId)}/logs'));

  @override
  Future<LogCollection> requestLogs(String hostId, {required int periodDays, required LogDetail detail}) async {
    final json = await _api.postJson('${_host(hostId)}/logs', body: {'periodDays': periodDays, 'detail': detail.wire});
    return logCollectionFromJson(json as Map<String, dynamic>);
  }

  @override
  Future<void> deleteLogs(String hostId, String id) => _api.delete('${_host(hostId)}/logs/${Uri.encodeComponent(id)}');

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
    final json = await _api.getJson(
      '${_host(hostId)}/logs/${Uri.encodeComponent(id)}/entries',
      query: {
        'severity': severity.wire,
        if (search != null && search.isNotEmpty) 'search': search,
        if (around != null) 'aroundUtc': around.toUtc().toIso8601String(),
        if (around != null) 'aroundMinutes': '$aroundMinutes',
        'skip': '$skip',
        'take': '$take',
      },
    );
    return logEntriesPageFromJson(json as Map<String, dynamic>);
  }

  @override
  Future<List<DeviceSearchResult>> search(String text, {int take = 10}) async =>
      deviceSearchResultsFromJson(await _api.getJson('$_base/search', query: {'q': text, 'take': '$take'}));

  @override
  Future<Uint8List> downloadLogs(String hostId, String id) =>
      _api.getBytes('${_host(hostId)}/logs/${Uri.encodeComponent(id)}/download');
}
