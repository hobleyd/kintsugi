import '../../core/network/json_reader.dart';
import '../../domain/entities/autonomy.dart';

AutonomyPeriod autonomyPeriodFromJson(Object? raw) => AutonomyPeriod.values.firstWhere(
      (p) => p.wire == raw,
      orElse: () => AutonomyPeriod.days30,
    );

int _int(Object? v) => (v as num?)?.toInt() ?? 0;
double _double(Object? v) => (v as num?)?.toDouble() ?? 0;

AutonomyCause _cause(Map<String, dynamic> j) => AutonomyCause(
      key: j['key'] as String? ?? '',
      title: j['title'] as String? ?? '',
      category: j['category'] as String? ?? 'Failure',
      measured: j['measured'] as bool? ?? false,
      devices: _int(j['devices']),
      deviceDays: _double(j['deviceDays']),
      sharePercent: _int(j['sharePercent']),
      explanation: j['explanation'] as String?,
      suggestedFix: j['suggestedFix'] as String?,
      patchNowConnected: _int(j['patchNowConnected']),
      patchNowOffline: _int(j['patchNowOffline']),
    );

AutonomyKpi _kpi(Map<String, dynamic> j) => AutonomyKpi(
      id: j['id'] as String? ?? '',
      category: j['category'] as String? ?? '',
      definition: j['definition'] as String? ?? '',
      direction: j['direction'] as String? ?? 'Information',
      isPercent: j['isPercent'] as bool? ?? false,
      green: _double(j['green']),
      amber: _double(j['amber']),
      value: _double(j['value']),
      numerator: _int(j['numerator']),
      denominator: _int(j['denominator']),
      status: j['status'] as String? ?? 'I',
      prior: (j['prior'] as num?)?.toDouble(),
      delta: (j['delta'] as num?)?.toDouble(),
    );

AutonomyDevice autonomyDeviceFromJson(Map<String, dynamic> j) => AutonomyDevice(
      hostId: j['hostId'] as String? ?? '',
      hostname: j['hostname'] as String? ?? '',
      detail: j['detail'] as String? ?? '',
      connected: j['connected'] as bool? ?? false,
      patchNowEligible: j['patchNowEligible'] as bool? ?? false,
    );

AutonomyDevicesPage autonomyDevicesPageFromJson(Map<String, dynamic> j) => AutonomyDevicesPage(
      total: _int(j['total']),
      items: listFromJson(j['items'], autonomyDeviceFromJson),
    );

AutonomyPatchNowResult autonomyPatchNowResultFromJson(Map<String, dynamic> j) => AutonomyPatchNowResult(
      started: _int(j['started']),
      queuedOffline: _int(j['queuedOffline']),
      notRequested: stringListFromJson(j['notRequested']),
    );

AutonomyDeviceHealth _health(Object? raw) {
  if (raw is! Map<String, dynamic>) return AutonomyDeviceHealth.none;
  return AutonomyDeviceHealth(
    unstable: _int(raw['unstable']),
    watch: _int(raw['watch']),
    items: listFromJson(
      raw['items'],
      (j) => AutonomyHealthDevice(
        hostId: j['hostId'] as String? ?? '',
        hostname: j['hostname'] as String? ?? '',
        status: j['status'] as String? ?? 'Watch',
        why: j['why'] as String? ?? '',
        lastEventUtc: dateTimeFromJson(j['lastEventUtc']),
      ),
    ),
  );
}

/// Reads an `AutonomyDto`. Hand-mirrored — see `.claude/rules/hand-mirrored-dtos.md`.
Autonomy autonomyFromJson(Map<String, dynamic> json) {
  final patches = json['patches'] as Map<String, dynamic>? ?? const {};
  return Autonomy(
    period: autonomyPeriodFromJson(json['period']),
    fromUtc: dateTimeRequiredFromJson(json['fromUtc']),
    toUtc: dateTimeRequiredFromJson(json['toUtc']),
    month: json['month'] as String?,
    historyStartsUtc: dateTimeFromJson(json['historyStartsUtc']),
    hostsWithEvents: _int(json['hostsWithEvents']),
    totalHosts: _int(json['totalHosts']),
    devicesDelayed: _int(json['devicesDelayed']),
    totalDeviceDays: _double(json['totalDeviceDays']),
    patches: AutonomyPatches(
      total: _int(patches['total']),
      ownSchedule: _int(patches['ownSchedule']),
      adminStarted: _int(patches['adminStarted']),
    ),
    buckets: listFromJson(
      json['buckets'],
      (j) => AutonomyBucket(startUtc: dateTimeRequiredFromJson(j['startUtc']), own: _int(j['own']), admin: _int(j['admin'])),
    ),
    months: listFromJson(
      json['months'],
      (j) => AutonomyMonth(month: j['month'] as String? ?? '', own: _int(j['own']), admin: _int(j['admin'])),
    ),
    causes: listFromJson(json['causes'], _cause),
    kpis: listFromJson(json['kpis'], _kpi),
    kpiHistory: listFromJson(
      json['kpiHistory'],
      (j) => AutonomyKpiHistory(
        id: j['id'] as String? ?? '',
        points: listFromJson(
          j['points'],
          (p) => AutonomyKpiPoint(dateUtc: dateTimeRequiredFromJson(p['dateUtc']), value: _double(p['value'])),
        ),
      ),
    ),
    deviceHealth: _health(json['deviceHealth']),
    events: listFromJson(
      json['events'],
      (j) => AutonomyEvent(
        occurredUtc: dateTimeRequiredFromJson(j['occurredUtc']),
        kind: j['kind'] as String? ?? '',
        subject: j['subject'] as String? ?? '',
        hostname: j['hostname'] as String? ?? '',
        actor: j['actor'] as String?,
        count: _int(j['count']) == 0 ? 1 : _int(j['count']),
      ),
    ),
  );
}
