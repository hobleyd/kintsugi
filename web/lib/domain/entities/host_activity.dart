import 'package:equatable/equatable.dart';

import 'enums.dart';
import 'patch_failure.dart';

/// What a timeline line is about. Mirrors `HostEventKind`, which the server sends by name.
enum HostEventKind {
  patchNowRequested,
  patchNowPickedUp,
  applicationPatched,
  operatingSystemPatched,
  patchFailed,
  failureDismissed,
  unexpectedShutdown,
  hardwareErrorFatal,
  hardwareErrorCorrected,
  logsRequested,
  logsCollected,
  logsDownloaded,
  logsDeleted,
  logsFailed;

  /// The filter tab it falls under.
  HostEventGroup get group => switch (this) {
        HostEventKind.patchFailed => HostEventGroup.failures,
        HostEventKind.unexpectedShutdown ||
        HostEventKind.hardwareErrorFatal ||
        HostEventKind.hardwareErrorCorrected =>
          HostEventGroup.health,
        HostEventKind.applicationPatched || HostEventKind.operatingSystemPatched => HostEventGroup.patched,
        _ => HostEventGroup.adminActions,
      };
}

enum HostEventGroup {
  failures('Failures'),
  health('Health'),
  patched('Patched'),
  adminActions('Admin actions');

  const HostEventGroup(this.label);

  final String label;
}

/// One line of a host's timeline. Mirrors `HostEventDto`.
class HostEvent extends Equatable {
  const HostEvent({
    required this.id,
    required this.occurredUtc,
    required this.kind,
    required this.subject,
    required this.fromVersion,
    required this.toVersion,
    required this.actor,
    required this.detail,
    required this.count,
  });

  final String id;
  final DateTime occurredUtc;
  final HostEventKind kind;

  /// An application name, or "Windows", "macOS" or "Linux".
  final String subject;
  final String? fromVersion;
  final String? toVersion;

  /// The administrator who caused it, or null when the host's agent did.
  final String? actor;

  /// Captured install output or an explanation.
  final String? detail;

  /// More than one for a failure that kept happening, folded into this line.
  final int count;

  /// The sentence the timeline shows.
  String get summary {
    final versions = fromVersion != null && toVersion != null ? ' $fromVersion → $toVersion' : '';
    return switch (kind) {
      HostEventKind.patchNowRequested => 'Patch now requested for $subject',
      HostEventKind.patchNowPickedUp => 'The host picked up the Patch now request for $subject',
      HostEventKind.applicationPatched => 'Patched $subject$versions',
      HostEventKind.operatingSystemPatched => 'Patched $subject',
      HostEventKind.patchFailed =>
        count > 1 ? 'Failed to install $subject ($count tries)' : 'Failed to install $subject',
      HostEventKind.failureDismissed => 'Dismissed the failure for $subject',
      HostEventKind.unexpectedShutdown => 'Shut down without being asked ($subject)',
      HostEventKind.hardwareErrorFatal => 'Fatal hardware error ($subject)',
      HostEventKind.hardwareErrorCorrected => 'Corrected hardware error ($subject)',
      HostEventKind.logsRequested => 'Logs requested from the device',
      HostEventKind.logsCollected => 'The device sent its logs',
      HostEventKind.logsDownloaded => 'Logs downloaded',
      HostEventKind.logsDeleted => 'Logs deleted',
      HostEventKind.logsFailed => 'The device could not send its logs',
    };
  }

  @override
  List<Object?> get props => [id, occurredUtc, kind, subject, fromVersion, toVersion, actor, detail, count];
}

/// An update waiting on the host. Mirrors `PendingUpdateDto`.
class PendingUpdate extends Equatable {
  const PendingUpdate({
    required this.name,
    required this.isOperatingSystem,
    required this.currentVersion,
    required this.availableVersion,
    required this.failed,
  });

  final String name;
  final bool isOperatingSystem;
  final String? currentVersion;
  final String? availableVersion;

  /// An open failure exists for it.
  final bool failed;

  @override
  List<Object?> get props => [name, isOperatingSystem, currentVersion, availableVersion, failed];
}

/// Everything the host activity page shows. Mirrors `HostActivityDto`.
class HostActivity extends Equatable {
  const HostActivity({
    required this.hostId,
    required this.hostname,
    required this.operatingSystem,
    required this.agentVersion,
    required this.status,
    required this.lastSeenUtc,
    required this.retentionMonths,
    required this.events,
    required this.pendingUpdates,
    required this.openFailures,
    required this.lastPatchedUtc,
    required this.patchedLast30Days,
    this.restartRequiredSinceUtc,
    this.restartRequiredBecause,
  });

  final String hostId;
  final String hostname;
  final String? operatingSystem;
  final String? agentVersion;
  final HostStatus status;
  final DateTime? lastSeenUtc;
  final int retentionMonths;
  final List<HostEvent> events;
  final List<PendingUpdate> pendingUpdates;
  final List<PatchFailure> openFailures;
  final DateTime? lastPatchedUtc;
  final int patchedLast30Days;

  /// When the host first said it needs a restart, or null. See `HostSummary.restartRequiredSinceUtc`.
  final DateTime? restartRequiredSinceUtc;
  final String? restartRequiredBecause;

  /// Tries across the open failures, which is the number worth showing: one failure that has
  /// happened five times is five failed tries.
  int get failedTries => openFailures.fold(0, (sum, f) => sum + f.failureCount);

  bool get isWindows => operatingSystem?.toLowerCase().contains('windows') ?? false;

  @override
  List<Object?> get props => [
        hostId, hostname, operatingSystem, agentVersion, status, lastSeenUtc, retentionMonths,
        events, pendingUpdates, openFailures, lastPatchedUtc, patchedLast30Days,
        restartRequiredSinceUtc, restartRequiredBecause,
      ];
}
