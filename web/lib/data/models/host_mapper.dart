import '../../core/network/json_reader.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/host.dart';

/// Reads a `HostDto`.
HostSummary hostFromJson(Map<String, dynamic> json) => HostSummary(
      id: json['id'] as String,
      hostname: json['hostname'] as String? ?? '',
      serialNumber: json['serialNumber'] as String? ?? '',
      operatingSystem: json['operatingSystem'] as String?,
      ipAddress: json['ipAddress'] as String?,
      status: enumFromJson(
        json['status'],
        HostStatus.values,
        const ['Unknown', 'Online', 'Offline', 'Decommissioned'],
        HostStatus.unknown,
      ),
      lastSeenUtc: dateTimeFromJson(json['lastSeenUtc']),
      agentVersion: json['agentVersion'] as String?,
      operatingSystemUpdateAvailable: json['operatingSystemUpdateAvailable'] as bool?,
      operatingSystemLatestVersion: json['operatingSystemLatestVersion'] as String?,
      appUpdatesAvailableCount: (json['appUpdatesAvailableCount'] as num?)?.toInt() ?? 0,
      removalRequested: json['removalRequested'] as bool? ?? false,
    );
