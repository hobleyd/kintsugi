import 'dart:typed_data';

import '../entities/device_report.dart';
import '../repositories/repositories.dart';

/// One device's report for the last 7 or 30 days.
class GetDeviceReport {
  const GetDeviceReport(this._repository);

  final DeviceReportRepository _repository;

  Future<DeviceReport> call(String hostId, {int days = 7}) => _repository.get(hostId, days: days);
}

/// The newest log collection without its content, which is what the page polls while it waits.
class GetLatestLogCollection {
  const GetLatestLogCollection(this._repository);

  final DeviceReportRepository _repository;

  Future<LogCollection?> call(String hostId) => _repository.latestLogs(hostId);
}

/// Asks the device's agent to collect its log at its next check-in.
class RequestLogCollection {
  const RequestLogCollection(this._repository);

  final DeviceReportRepository _repository;

  Future<LogCollection> call(String hostId, {required int periodDays, required LogDetail detail}) =>
      _repository.requestLogs(hostId, periodDays: periodDays, detail: detail);
}

/// Deletes a collected log now, or cancels a request nobody has picked up.
class DeleteLogCollection {
  const DeleteLogCollection(this._repository);

  final DeviceReportRepository _repository;

  Future<void> call(String hostId, String id) => _repository.deleteLogs(hostId, id);
}

/// A page of a collection's entries, newest first, filtered on the server.
class GetLogEntries {
  const GetLogEntries(this._repository);

  final DeviceReportRepository _repository;

  Future<LogEntriesPage> call(
    String hostId,
    String id, {
    LogSeverityFilter severity = LogSeverityFilter.all,
    String? search,
    DateTime? around,
    int aroundMinutes = 10,
    int skip = 0,
    int take = 150,
  }) =>
      _repository.entries(
        hostId,
        id,
        severity: severity,
        search: search,
        around: around,
        aroundMinutes: aroundMinutes,
        skip: skip,
        take: take,
      );
}

/// The collected log as a zip.
class DownloadLogCollection {
  const DownloadLogCollection(this._repository);

  final DeviceReportRepository _repository;

  Future<Uint8List> call(String hostId, String id) => _repository.downloadLogs(hostId, id);
}

/// Finds devices by name or serial number, so any device's report can be opened.
class SearchDevices {
  const SearchDevices(this._repository);

  final DeviceReportRepository _repository;

  Future<List<DeviceSearchResult>> call(String text, {int take = 10}) => _repository.search(text, take: take);
}
