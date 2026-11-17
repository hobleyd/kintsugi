import 'package:equatable/equatable.dart';

/// What a health event on the report says happened. Mirrors the server's event kinds by name.
enum DeviceReportEventKind {
  unexpectedShutdown('Unexpected shutdown'),
  hardwareErrorFatal('Fatal hardware error'),
  hardwareErrorCorrected('Corrected hardware error');

  const DeviceReportEventKind(this.label);

  final String label;
}

/// What Kintsugi did on the device: an application install, or a system update (which can restart it).
enum KintsugiActionKind { install, osUpdate }

enum DeviceHealthStatus { healthy, watch, unstable }

enum HypothesisConfidence {
  high('High confidence', 3),
  medium('Medium confidence', 2),
  low('Low confidence', 1),
  unlikely('Unlikely', 1);

  const HypothesisConfidence(this.label, this.pips);

  final String label;

  /// How many of the three marks of the meter are filled.
  final int pips;
}

enum DevicePlatform { windows, macOS, linux, unknown }

/// A collection's state as the server reports it, with its time-out already applied.
enum LogCollectionStatus { requested, collecting, completed, failed }

/// How much of the log the agent keeps.
enum LogDetail {
  warningsAndErrors('WarningsAndErrors', 'warnings and errors'),
  everything('Everything', 'everything');

  const LogDetail(this.wire, this.label);

  final String wire;
  final String label;
}

/// The severity buttons over the entries viewer, which the server filters.
enum LogSeverityFilter {
  all('all', 'All'),
  errors('errors', 'Errors'),
  warnings('warnings', 'Warnings');

  const LogSeverityFilter(this.wire, this.label);

  final String wire;
  final String label;
}

enum LogSeverity { critical, error, warning, info }

class DeviceReportHost extends Equatable {
  const DeviceReportHost({
    required this.id,
    required this.hostname,
    required this.operatingSystem,
    required this.platform,
    required this.serialNumber,
    required this.agentVersion,
    required this.lastSeenUtc,
  });

  final String id;
  final String hostname;
  final String? operatingSystem;
  final DevicePlatform platform;
  final String serialNumber;
  final String? agentVersion;
  final DateTime? lastSeenUtc;

  @override
  List<Object?> get props => [id, hostname, operatingSystem, platform, serialNumber, agentVersion, lastSeenUtc];
}

class DeviceReportHealth extends Equatable {
  const DeviceReportHealth({
    required this.status,
    required this.why,
    required this.shutdowns7d,
    required this.shutdowns30d,
    required this.fatalErrors30d,
    required this.correctedErrors30d,
  });

  final DeviceHealthStatus status;
  final String why;
  final int shutdowns7d;
  final int shutdowns30d;
  final int fatalErrors30d;
  final int correctedErrors30d;

  @override
  List<Object?> get props => [status, why, shutdowns7d, shutdowns30d, fatalErrors30d, correctedErrors30d];
}

/// The nearest Kintsugi action before a shutdown. [within30Minutes] is the server's own verdict and the
/// only thing that makes Kintsugi "possibly involved".
class DeviceReportNearby extends Equatable {
  const DeviceReportNearby({
    required this.action,
    required this.subject,
    required this.atUtc,
    required this.minutesBefore,
    required this.within30Minutes,
  });

  final KintsugiActionKind action;
  final String subject;
  final DateTime atUtc;
  final double minutesBefore;
  final bool within30Minutes;

  @override
  List<Object?> get props => [action, subject, atUtc, minutesBefore, within30Minutes];
}

class DeviceReportEvent extends Equatable {
  const DeviceReportEvent({
    required this.occurredUtc,
    required this.kind,
    required this.source,
    required this.detail,
    required this.kintsugiNearby,
  });

  final DateTime occurredUtc;
  final DeviceReportEventKind kind;
  final String source;
  final String? detail;

  /// Only for a shutdown, and null when Kintsugi did nothing before it.
  final DeviceReportNearby? kintsugiNearby;

  bool get isShutdown => kind == DeviceReportEventKind.unexpectedShutdown;

  @override
  List<Object?> get props => [occurredUtc, kind, source, detail, kintsugiNearby];
}

class DeviceReportKintsugiAction extends Equatable {
  const DeviceReportKintsugiAction({required this.atUtc, required this.kind, required this.subject});

  final DateTime atUtc;
  final KintsugiActionKind kind;
  final String subject;

  @override
  List<Object?> get props => [atUtc, kind, subject];
}

class DeviceHypothesis extends Equatable {
  const DeviceHypothesis({
    required this.title,
    required this.confidence,
    required this.evidence,
    required this.checkNext,
  });

  final String title;
  final HypothesisConfidence confidence;
  final List<String> evidence;
  final List<String> checkNext;

  @override
  List<Object?> get props => [title, confidence, evidence, checkNext];
}

class DeviceInstallRun extends Equatable {
  const DeviceInstallRun({required this.atUtc, required this.subject, required this.succeeded, required this.output});

  final DateTime atUtc;
  final String subject;
  final bool succeeded;
  final String? output;

  @override
  List<Object?> get props => [atUtc, subject, succeeded, output];
}

/// One log collection, without its content. Mirrors `LogCollectionDto`.
class LogCollection extends Equatable {
  const LogCollection({
    required this.id,
    required this.status,
    required this.requestedUtc,
    required this.requestedBy,
    required this.periodDays,
    required this.detail,
    required this.expiresUtc,
    required this.claimedUtc,
    required this.completedUtc,
    required this.failureReason,
    required this.entryCount,
    required this.sizeBytes,
    required this.truncatedBeforeUtc,
    required this.keepUntilUtc,
  });

  final String id;
  final LogCollectionStatus status;
  final DateTime requestedUtc;
  final String requestedBy;
  final int periodDays;
  final LogDetail detail;
  final DateTime expiresUtc;
  final DateTime? claimedUtc;
  final DateTime? completedUtc;
  final String? failureReason;
  final int entryCount;
  final int sizeBytes;

  /// Set when the 5 MB cap cut the log: nothing before this is included.
  final DateTime? truncatedBeforeUtc;
  final DateTime keepUntilUtc;

  /// Still waiting on the device, so the page polls.
  bool get isOpen => status == LogCollectionStatus.requested || status == LogCollectionStatus.collecting;

  bool get isCompleted => status == LogCollectionStatus.completed;

  @override
  List<Object?> get props => [
        id, status, requestedUtc, requestedBy, periodDays, detail, expiresUtc, claimedUtc, completedUtc,
        failureReason, entryCount, sizeBytes, truncatedBeforeUtc, keepUntilUtc,
      ];
}

class LogEntry extends Equatable {
  const LogEntry({required this.time, required this.severity, required this.source, required this.message});

  final DateTime time;
  final LogSeverity severity;
  final String source;
  final String message;

  @override
  List<Object?> get props => [time, severity, source, message];
}

class LogEntriesPage extends Equatable {
  const LogEntriesPage({required this.total, required this.items});

  final int total;
  final List<LogEntry> items;

  @override
  List<Object?> get props => [total, items];
}

/// One match from the "Find a device" search. Mirrors the server's search result.
class DeviceSearchResult extends Equatable {
  const DeviceSearchResult({
    required this.hostId,
    required this.hostname,
    required this.operatingSystem,
    required this.platform,
    this.status = DeviceHealthStatus.healthy,
    this.why = '',
    this.lastEventUtc,
  });

  final String hostId;
  final String hostname;
  final String? operatingSystem;
  final DevicePlatform platform;

  /// The same rule as the Device health list.
  final DeviceHealthStatus status;

  /// The reason in words; empty for a healthy device.
  final String why;

  /// The latest health event, or null when there is none.
  final DateTime? lastEventUtc;

  @override
  List<Object?> get props => [hostId, hostname, operatingSystem, platform, status, why, lastEventUtc];
}

/// Everything the device report page shows. Mirrors `DeviceReportDto`.
class DeviceReport extends Equatable {
  const DeviceReport({
    required this.host,
    required this.days,
    required this.health,
    required this.events,
    required this.kintsugiActions,
    required this.shutdownsNearKintsugi,
    required this.hypotheses,
    required this.installRuns,
    required this.logs,
    this.lastCompletedLogs,
  });

  final DeviceReportHost host;
  final int days;
  final DeviceReportHealth health;
  final List<DeviceReportEvent> events;
  final List<DeviceReportKintsugiAction> kintsugiActions;
  final int shutdownsNearKintsugi;
  final List<DeviceHypothesis> hypotheses;
  final List<DeviceInstallRun> installRuns;

  /// The newest collection, or null when none was ever requested.
  final LogCollection? logs;

  /// The last collection that completed, whatever the newest one is doing. A newer request that is waiting
  /// or has failed does not hide it: the Logs panel keeps showing it, and the hypotheses read it. Null when
  /// no collection ever completed. The same collection as [logs] when that one is completed.
  final LogCollection? lastCompletedLogs;

  List<DeviceReportEvent> get shutdowns => events.where((e) => e.isShutdown).toList(growable: false);

  @override
  List<Object?> get props => [
        host, days, health, events, kintsugiActions, shutdownsNearKintsugi, hypotheses, installRuns, logs, lastCompletedLogs,
      ];
}
