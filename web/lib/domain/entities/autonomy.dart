import 'package:equatable/equatable.dart';

/// Which period the Autonomy page shows. The wire values are `7d`, `30d` and `month`.
enum AutonomyPeriod {
  days7('7d', 'Last 7 days'),
  days30('30d', 'Last 30 days'),
  month('month', 'By month');

  const AutonomyPeriod(this.wire, this.label);

  final String wire;
  final String label;
}

class AutonomyPatches extends Equatable {
  const AutonomyPatches({required this.total, required this.ownSchedule, required this.adminStarted});

  final int total;

  /// Patches that ran on the host's own schedule. Never "the host decided": a patch result does not
  /// say whether the run was scheduled or forced, so this is everything an administrator did not
  /// start with Patch now. See `AutonomyPatchClassifier`.
  final int ownSchedule;
  final int adminStarted;

  int percent(int part) => total == 0 ? 0 : (part * 100 / total).round();

  @override
  List<Object?> get props => [total, ownSchedule, adminStarted];
}

class AutonomyBucket extends Equatable {
  const AutonomyBucket({required this.startUtc, required this.own, required this.admin});

  final DateTime startUtc;
  final int own;
  final int admin;

  int get total => own + admin;

  @override
  List<Object?> get props => [startUtc, own, admin];
}

class AutonomyMonth extends Equatable {
  const AutonomyMonth({required this.month, required this.own, required this.admin});

  /// "2026-11".
  final String month;
  final int own;
  final int admin;

  int get total => own + admin;

  @override
  List<Object?> get props => [month, own, admin];
}

class AutonomyCause extends Equatable {
  const AutonomyCause({
    required this.key,
    required this.title,
    required this.category,
    required this.measured,
    required this.devices,
    required this.deviceDays,
    required this.sharePercent,
    required this.explanation,
    required this.suggestedFix,
    required this.patchNowConnected,
    required this.patchNowOffline,
  });

  /// Fixed: "disconnected", "restart", "maintenance", or "failure:" plus the cause.
  final String key;
  final String title;

  /// "Disconnected", "Restart", "Maintenance" or "Failure".
  final String category;

  /// False when Kintsugi cannot see this kind yet (maintenance mode does not exist), so an empty row is
  /// not read as good news.
  final bool measured;
  final int devices;

  /// Days of waiting added over every device in it: how much it slows patching down.
  final double deviceDays;
  final int sharePercent;
  final String? explanation;
  final String? suggestedFix;

  /// Windows devices Patch now can start right now, and ones that would take it when they check in.
  final int patchNowConnected;
  final int patchNowOffline;

  bool get isFailure => category == 'Failure';
  bool get canPatchNow => patchNowConnected + patchNowOffline > 0;

  @override
  List<Object?> get props => [
        key, title, category, measured, devices, deviceDays, sharePercent, explanation, suggestedFix,
        patchNowConnected, patchNowOffline,
      ];
}

/// One key figure against its band. Mirrors `AutonomyKpiDto`. The bands are fixed.
class AutonomyKpi extends Equatable {
  const AutonomyKpi({
    required this.id,
    required this.category,
    required this.definition,
    required this.direction,
    required this.isPercent,
    required this.green,
    required this.amber,
    required this.value,
    required this.numerator,
    required this.denominator,
    required this.status,
    required this.prior,
    required this.delta,
  });

  final String id;
  final String category;
  final String definition;

  /// "LowerIsBetter", "HigherIsBetter" or "Information".
  final String direction;
  final bool isPercent;
  final double green;
  final double amber;
  final double value;
  final int numerator;
  final int denominator;

  /// "G", "A", "R", or "I" for a figure with no band.
  final String status;
  final double? prior;
  final double? delta;

  bool get isInformation => direction == 'Information';

  @override
  List<Object?> get props => [
        id, category, definition, direction, isPercent, green, amber, value, numerator, denominator,
        status, prior, delta,
      ];
}

class AutonomyKpiPoint extends Equatable {
  const AutonomyKpiPoint({required this.dateUtc, required this.value});

  final DateTime dateUtc;
  final double value;

  @override
  List<Object?> get props => [dateUtc, value];
}

class AutonomyKpiHistory extends Equatable {
  const AutonomyKpiHistory({required this.id, required this.points});

  final String id;
  final List<AutonomyKpiPoint> points;

  @override
  List<Object?> get props => [id, points];
}

/// One device that shut down by itself or logged hardware errors. Mirrors `AutonomyHealthDeviceDto`.
class AutonomyHealthDevice extends Equatable {
  const AutonomyHealthDevice({
    required this.hostId,
    required this.hostname,
    required this.status,
    required this.why,
    required this.lastEventUtc,
  });

  final String hostId;
  final String hostname;

  /// "Unstable" or "Watch". A healthy device is not listed.
  final String status;

  /// The counts that put it there, in words.
  final String why;
  final DateTime? lastEventUtc;

  bool get isUnstable => status == 'Unstable';

  @override
  List<Object?> get props => [hostId, hostname, status, why, lastEventUtc];
}

/// Devices that shut down by themselves or logged hardware errors, read from each operating system's own
/// log. The counts are the whole fleet's; [items] are the worst few, so the page does not grow with it.
class AutonomyDeviceHealth extends Equatable {
  const AutonomyDeviceHealth({required this.unstable, required this.watch, required this.items});

  static const none = AutonomyDeviceHealth(unstable: 0, watch: 0, items: []);

  final int unstable;
  final int watch;
  final List<AutonomyHealthDevice> items;

  @override
  List<Object?> get props => [unstable, watch, items];
}

class AutonomyEvent extends Equatable {
  const AutonomyEvent({
    required this.occurredUtc,
    required this.kind,
    required this.subject,
    required this.hostname,
    required this.actor,
    required this.count,
  });

  final DateTime occurredUtc;
  final String kind;
  final String subject;
  final String hostname;
  final String? actor;
  final int count;

  @override
  List<Object?> get props => [occurredUtc, kind, subject, hostname, actor, count];
}

class AutonomyDevice extends Equatable {
  const AutonomyDevice({
    required this.hostId,
    required this.hostname,
    required this.detail,
    required this.connected,
    required this.patchNowEligible,
  });

  final String hostId;
  final String hostname;
  final String detail;
  final bool connected;
  final bool patchNowEligible;

  @override
  List<Object?> get props => [hostId, hostname, detail, connected, patchNowEligible];
}

class AutonomyDevicesPage extends Equatable {
  const AutonomyDevicesPage({required this.total, required this.items});

  final int total;
  final List<AutonomyDevice> items;

  @override
  List<Object?> get props => [total, items];
}

class AutonomyPatchNowResult extends Equatable {
  const AutonomyPatchNowResult({required this.started, required this.queuedOffline, required this.notRequested});

  final int started;
  final int queuedOffline;
  final List<String> notRequested;

  @override
  List<Object?> get props => [started, queuedOffline, notRequested];
}

/// Everything the Autonomy page shows. Mirrors `AutonomyDto`.
class Autonomy extends Equatable {
  const Autonomy({
    required this.period,
    required this.fromUtc,
    required this.toUtc,
    required this.month,
    required this.historyStartsUtc,
    required this.hostsWithEvents,
    required this.totalHosts,
    required this.devicesDelayed,
    required this.totalDeviceDays,
    required this.patches,
    required this.buckets,
    required this.months,
    required this.causes,
    required this.kpis,
    required this.kpiHistory,
    required this.events,
    this.deviceHealth = AutonomyDeviceHealth.none,
  });

  final AutonomyPeriod period;
  final DateTime fromUtc;
  final DateTime toUtc;
  final String? month;
  final DateTime? historyStartsUtc;
  final int hostsWithEvents;
  final int totalHosts;
  final int devicesDelayed;
  final double totalDeviceDays;
  final AutonomyPatches patches;
  final List<AutonomyBucket> buckets;
  final List<AutonomyMonth> months;
  final List<AutonomyCause> causes;
  final List<AutonomyKpi> kpis;
  final List<AutonomyKpiHistory> kpiHistory;
  final List<AutonomyEvent> events;
  final AutonomyDeviceHealth deviceHealth;

  List<AutonomyKpiPoint> historyOf(String kpiId) =>
      kpiHistory.where((h) => h.id == kpiId).expand((h) => h.points).toList();

  int get failedDevices => kpis.where((k) => k.id == 'failures.open_failure_devices').map((k) => k.numerator).firstOrNull ?? 0;

  @override
  List<Object?> get props => [
        period, fromUtc, toUtc, month, historyStartsUtc, hostsWithEvents, totalHosts, devicesDelayed,
        totalDeviceDays, patches, buckets, months, causes, kpis, kpiHistory, events, deviceHealth,
      ];
}
