import '../../core/network/json_reader.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/host_activity.dart';
import 'patch_failure_mapper.dart';

const _kindNames = [
  'PatchNowRequested',
  'PatchNowPickedUp',
  'ApplicationPatched',
  'OperatingSystemPatched',
  'PatchFailed',
  'FailureDismissed',
  'UnexpectedShutdown',
  'HardwareErrorFatal',
  'HardwareErrorCorrected',
  'LogsRequested',
  'LogsCollected',
  'LogsDownloaded',
  'LogsDeleted',
  'LogsFailed',
];

HostEventKind hostEventKindFromJson(Object? raw) =>
    enumFromJson(raw, HostEventKind.values, _kindNames, HostEventKind.patchFailed);

/// Reads a `HostEventDto`. Hand-mirrored — see `.claude/rules/hand-mirrored-dtos.md`.
HostEvent hostEventFromJson(Map<String, dynamic> json) => HostEvent(
      id: json['id'] as String? ?? '',
      occurredUtc: dateTimeRequiredFromJson(json['occurredUtc']),
      kind: hostEventKindFromJson(json['kind']),
      subject: json['subject'] as String? ?? '',
      fromVersion: json['fromVersion'] as String?,
      toVersion: json['toVersion'] as String?,
      actor: json['actor'] as String?,
      detail: json['detail'] as String?,
      count: (json['count'] as num?)?.toInt() ?? 1,
    );

PendingUpdate pendingUpdateFromJson(Map<String, dynamic> json) => PendingUpdate(
      name: json['name'] as String? ?? '',
      isOperatingSystem: json['kind'] == 'OperatingSystem',
      currentVersion: json['currentVersion'] as String?,
      availableVersion: json['availableVersion'] as String?,
      failed: json['failed'] as bool? ?? false,
    );

HostStatus _statusFromJson(Object? raw) => enumFromJson(
      raw,
      HostStatus.values,
      const ['Unknown', 'Online', 'Offline', 'Decommissioned'],
      HostStatus.unknown,
    );

/// Reads a `HostActivityDto`.
HostActivity hostActivityFromJson(Map<String, dynamic> json) => HostActivity(
      hostId: json['hostId'] as String? ?? '',
      hostname: json['hostname'] as String? ?? '',
      operatingSystem: json['operatingSystem'] as String?,
      agentVersion: json['agentVersion'] as String?,
      status: _statusFromJson(json['status']),
      lastSeenUtc: dateTimeFromJson(json['lastSeenUtc']),
      retentionMonths: (json['retentionMonths'] as num?)?.toInt() ?? 6,
      events: listFromJson(json['events'], hostEventFromJson),
      pendingUpdates: listFromJson(json['pendingUpdates'], pendingUpdateFromJson),
      openFailures: listFromJson(json['openFailures'], patchFailureFromJson),
      lastPatchedUtc: dateTimeFromJson(json['lastPatchedUtc']),
      patchedLast30Days: (json['patchedLast30Days'] as num?)?.toInt() ?? 0,
      restartRequiredSinceUtc: dateTimeFromJson(json['restartRequiredSinceUtc']),
      restartRequiredBecause: json['restartRequiredBecause'] as String?,
    );
