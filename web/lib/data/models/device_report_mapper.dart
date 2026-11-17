import '../../core/network/json_reader.dart';
import '../../domain/entities/device_report.dart';

/// Reads the device report. Hand-mirrored from `DeviceReportDtos.cs` and `LogCollectionDtos.cs` — see
/// `.claude/rules/hand-mirrored-dtos.md`. Every enum crosses the wire by name.
DeviceReportEventKind deviceReportEventKindFromJson(Object? raw) => enumFromJson(
      raw,
      DeviceReportEventKind.values,
      const ['UnexpectedShutdown', 'HardwareErrorFatal', 'HardwareErrorCorrected'],
      DeviceReportEventKind.unexpectedShutdown,
    );

KintsugiActionKind kintsugiActionKindFromJson(Object? raw) =>
    enumFromJson(raw, KintsugiActionKind.values, const ['Install', 'OsUpdate'], KintsugiActionKind.install);

DeviceHealthStatus deviceHealthStatusFromJson(Object? raw) =>
    enumFromJson(raw, DeviceHealthStatus.values, const ['Healthy', 'Watch', 'Unstable'], DeviceHealthStatus.healthy);

HypothesisConfidence hypothesisConfidenceFromJson(Object? raw) => enumFromJson(
      raw,
      HypothesisConfidence.values,
      const ['High', 'Medium', 'Low', 'Unlikely'],
      HypothesisConfidence.low,
    );

DevicePlatform devicePlatformFromJson(Object? raw) => enumFromJson(
      raw,
      DevicePlatform.values,
      const ['Windows', 'macOS', 'Linux', 'Unknown'],
      DevicePlatform.unknown,
    );

LogCollectionStatus logCollectionStatusFromJson(Object? raw) => enumFromJson(
      raw,
      LogCollectionStatus.values,
      const ['Requested', 'Collecting', 'Completed', 'Failed'],
      LogCollectionStatus.failed,
    );

LogDetail logDetailFromJson(Object? raw) => enumFromJson(
      raw,
      LogDetail.values,
      const ['WarningsAndErrors', 'Everything'],
      LogDetail.warningsAndErrors,
    );

LogSeverity logSeverityFromJson(Object? raw) => enumFromJson(
      raw,
      LogSeverity.values,
      const ['Critical', 'Error', 'Warning', 'Info'],
      LogSeverity.info,
    );

int _int(Object? v) => (v as num?)?.toInt() ?? 0;

DeviceReportNearby? _nearby(Object? raw) {
  if (raw is! Map<String, dynamic>) return null;
  return DeviceReportNearby(
    action: kintsugiActionKindFromJson(raw['action']),
    subject: raw['subject'] as String? ?? '',
    atUtc: dateTimeRequiredFromJson(raw['atUtc']),
    minutesBefore: (raw['minutesBefore'] as num?)?.toDouble() ?? 0,
    within30Minutes: raw['within30Minutes'] as bool? ?? false,
  );
}

/// Reads a `LogCollectionDto`.
LogCollection logCollectionFromJson(Map<String, dynamic> j) => LogCollection(
      id: j['id'] as String? ?? '',
      status: logCollectionStatusFromJson(j['status']),
      requestedUtc: dateTimeRequiredFromJson(j['requestedUtc']),
      requestedBy: j['requestedBy'] as String? ?? '',
      periodDays: _int(j['periodDays']),
      detail: logDetailFromJson(j['detail']),
      expiresUtc: dateTimeRequiredFromJson(j['expiresUtc']),
      claimedUtc: dateTimeFromJson(j['claimedUtc']),
      completedUtc: dateTimeFromJson(j['completedUtc']),
      failureReason: j['failureReason'] as String?,
      entryCount: _int(j['entryCount']),
      sizeBytes: _int(j['sizeBytes']),
      truncatedBeforeUtc: dateTimeFromJson(j['truncatedBeforeUtc']),
      keepUntilUtc: dateTimeRequiredFromJson(j['keepUntilUtc']),
    );

/// The latest-collection route answers 204 with no body when there is none, which `ApiClient` returns
/// as null.
LogCollection? logCollectionOrNullFromJson(Object? raw) =>
    raw is Map<String, dynamic> ? logCollectionFromJson(raw) : null;

/// Reads a `LogEntriesPageDto`.
LogEntriesPage logEntriesPageFromJson(Map<String, dynamic> json) => LogEntriesPage(
      total: _int(json['total']),
      items: listFromJson(
        json['items'],
        (j) => LogEntry(
          time: dateTimeRequiredFromJson(j['t']),
          severity: logSeverityFromJson(j['sev']),
          source: j['src'] as String? ?? '',
          message: j['msg'] as String? ?? '',
        ),
      ),
    );

/// Reads one search result. Hand-mirrored from the search action in `AdminDeviceReportController`.
DeviceSearchResult deviceSearchResultFromJson(Map<String, dynamic> j) => DeviceSearchResult(
      hostId: j['hostId'] as String? ?? '',
      hostname: j['hostname'] as String? ?? '',
      operatingSystem: j['operatingSystem'] as String?,
      platform: devicePlatformFromJson(j['platform']),
      status: deviceHealthStatusFromJson(j['status']),
      why: j['why'] as String? ?? '',
      lastEventUtc: dateTimeFromJson(j['lastEventUtc']),
    );

/// The search answers a bare JSON array.
List<DeviceSearchResult> deviceSearchResultsFromJson(Object? raw) => listFromJson(raw, deviceSearchResultFromJson);

/// Reads a `DeviceReportDto`.
DeviceReport deviceReportFromJson(Map<String, dynamic> json) {
  final host = json['host'] as Map<String, dynamic>? ?? const {};
  final health = json['health'] as Map<String, dynamic>? ?? const {};

  return DeviceReport(
    host: DeviceReportHost(
      id: host['id'] as String? ?? '',
      hostname: host['hostname'] as String? ?? '',
      operatingSystem: host['operatingSystem'] as String?,
      platform: devicePlatformFromJson(host['platform']),
      serialNumber: host['serialNumber'] as String? ?? '',
      agentVersion: host['agentVersion'] as String?,
      lastSeenUtc: dateTimeFromJson(host['lastSeenUtc']),
    ),
    days: _int(json['days']) == 0 ? 7 : _int(json['days']),
    health: DeviceReportHealth(
      status: deviceHealthStatusFromJson(health['status']),
      why: health['why'] as String? ?? '',
      shutdowns7d: _int(health['shutdowns7d']),
      shutdowns30d: _int(health['shutdowns30d']),
      fatalErrors30d: _int(health['fatalErrors30d']),
      correctedErrors30d: _int(health['correctedErrors30d']),
    ),
    events: listFromJson(
      json['events'],
      (j) => DeviceReportEvent(
        occurredUtc: dateTimeRequiredFromJson(j['occurredUtc']),
        kind: deviceReportEventKindFromJson(j['kind']),
        source: j['source'] as String? ?? '',
        detail: j['detail'] as String?,
        kintsugiNearby: _nearby(j['kintsugiNearby']),
      ),
    ),
    kintsugiActions: listFromJson(
      json['kintsugiActions'],
      (j) => DeviceReportKintsugiAction(
        atUtc: dateTimeRequiredFromJson(j['atUtc']),
        kind: kintsugiActionKindFromJson(j['kind']),
        subject: j['subject'] as String? ?? '',
      ),
    ),
    shutdownsNearKintsugi: _int(json['shutdownsNearKintsugi']),
    hypotheses: listFromJson(
      json['hypotheses'],
      (j) => DeviceHypothesis(
        title: j['title'] as String? ?? '',
        confidence: hypothesisConfidenceFromJson(j['confidence']),
        evidence: stringListFromJson(j['evidence']),
        checkNext: stringListFromJson(j['checkNext']),
      ),
    ),
    installRuns: listFromJson(
      json['installRuns'],
      (j) => DeviceInstallRun(
        atUtc: dateTimeRequiredFromJson(j['atUtc']),
        subject: j['subject'] as String? ?? '',
        succeeded: j['succeeded'] as bool? ?? false,
        output: j['output'] as String?,
      ),
    ),
    logs: logCollectionOrNullFromJson(json['logs']),
    lastCompletedLogs: logCollectionOrNullFromJson(json['lastCompletedLogs']),
  );
}
