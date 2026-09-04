import 'package:equatable/equatable.dart';

import 'enums.dart';

/// A host in the fleet, as the Hosts screen lists it. Mirrors `HostDto`.
class HostSummary extends Equatable {
  const HostSummary({
    required this.id,
    required this.hostname,
    required this.serialNumber,
    required this.operatingSystem,
    required this.ipAddress,
    required this.status,
    required this.lastSeenUtc,
    required this.agentVersion,
    required this.operatingSystemUpdateAvailable,
    required this.operatingSystemLatestVersion,
    required this.appUpdatesAvailableCount,
    required this.removalRequested,
  });

  final String id;
  final String hostname;

  /// The host's own identity — it becomes the certificate CN every authenticated request is
  /// checked against. On Windows and Linux it is frequently a placeholder the agent refuses to
  /// enroll with, which is why it is worth showing rather than hiding as an internal key.
  final String serialNumber;

  final String? operatingSystem;
  final String? ipAddress;
  final HostStatus status;
  final DateTime? lastSeenUtc;

  /// The version the host's agent reported on its last check-in, mirroring
  /// `HostDto.AgentVersion`. Null when the agent predates the field and has never reported one —
  /// the server keeps whatever it last heard rather than clearing it, so null means "never", not
  /// "not this time".
  final String? agentVersion;

  /// Tri-state on purpose: null is "not checked", which is a different thing from "up to date"
  /// and has to read differently on screen.
  final bool? operatingSystemUpdateAvailable;

  final String? operatingSystemLatestVersion;
  final int appUpdatesAvailableCount;

  /// True once removal has been requested and the agent has not yet confirmed it uninstalled
  /// itself.
  final bool removalRequested;

  @override
  List<Object?> get props => [
        id,
        hostname,
        serialNumber,
        operatingSystem,
        ipAddress,
        status,
        lastSeenUtc,
        agentVersion,
        operatingSystemUpdateAvailable,
        operatingSystemLatestVersion,
        appUpdatesAvailableCount,
        removalRequested,
      ];
}
