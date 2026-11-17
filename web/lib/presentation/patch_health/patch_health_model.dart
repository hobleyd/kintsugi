import 'package:equatable/equatable.dart';

import '../../domain/entities/enums.dart';
import '../../domain/entities/host.dart';
import '../../domain/entities/patch_failure.dart';

/// What kind of trouble a host is in, which is what the Patch Health tabs split on.
enum HealthKind {
  /// The operating system's own update ran and failed.
  osUpdate,

  /// An application's upgrade script ran and failed.
  application,

  /// Updates are pending and the host has not been seen, so nothing can be tried.
  offline,
}

/// One thing wrong with one host, with the reason in words.
///
/// Built from what the server already knows — the outstanding Failed Updates rows and the Hosts
/// list — so this screen invents no data of its own. In particular there is no "user connected"
/// here: the agents do not tell the server that yet, and a column that guessed would be worse than
/// no column.
class HealthItem extends Equatable {
  const HealthItem({
    required this.key,
    required this.kind,
    required this.hostId,
    required this.hostname,
    required this.operatingSystem,
    required this.subject,
    required this.cause,
    required this.explanation,
    required this.suggestedFix,
    required this.tries,
    required this.since,
    required this.details,
    this.osUpdatePending = false,
  });

  /// Unique per row, for keying the expanded panel.
  final String key;
  final HealthKind kind;
  final String hostId;
  final String hostname;
  final String? operatingSystem;

  /// What is failing: "Windows Update", an application's name, or "Pending updates".
  final String subject;

  /// Why, in a few words.
  final String cause;
  final String explanation;
  final String suggestedFix;

  /// Failed attempts, or 0 for an offline host that nothing has been tried on.
  final int tries;

  /// When this started, on the host's own clock: the first failure, or the last time it was seen.
  final DateTime? since;

  /// The installer's captured output, or null when there is none to show.
  final String? details;

  /// Whether the host has an operating system update waiting, which is what "Patch now" installs.
  /// Known for an operating system failure (that is what failed) and for an offline host from the
  /// Hosts list; never for an application failure.
  final bool osUpdatePending;

  /// Whether the Patch now button can act on this row.
  ///
  /// Windows hosts only, for now: the Windows agent is the one that knows how to install the update
  /// and restart on request, and an instruction a macOS or Linux agent cannot act on would be
  /// dropped without a word. And only a row about the operating system: an application failure is
  /// repaired from Failed Updates, where the script is.
  bool get canPatchNow =>
      (operatingSystem?.toLowerCase().contains('windows') ?? false) &&
      (kind == HealthKind.osUpdate || (kind == HealthKind.offline && osUpdatePending));

  @override
  List<Object?> get props => [
        key,
        kind,
        hostId,
        hostname,
        operatingSystem,
        subject,
        cause,
        explanation,
        suggestedFix,
        tries,
        since,
        details,
        osUpdatePending,
      ];
}

/// The application name an operating system patch-now travels under. The Windows agent files a failed
/// update under the same name and takes a forced run for it as "install the Windows update now and
/// restart"; see `OS_FAILURE_APPLICATION_NAME` in its `os_update.rs`.
const windowsPatchNowName = 'Windows';
const windowsPatchNowPlatform = 'Windows';

/// Turns the outstanding failures and the host list into the rows Patch Health shows, worst first:
/// operating system failures, then application failures, then offline hosts; within each, the
/// most-repeated and the longest-running first.
///
/// An offline host is listed only when it has updates waiting, and not when it is already listed for
/// an operating system failure — it would be one problem shown twice.
List<HealthItem> buildHealthItems({
  required List<PatchFailure> failures,
  required List<HostSummary> hosts,
}) {
  final hostsById = {for (final host in hosts) host.id: host};
  final items = <HealthItem>[];
  final hostsWithOsFailure = <String>{};

  for (final failure in failures.where((f) => f.isOutstanding)) {
    final host = hostsById[failure.hostId];
    final isOs = failure.isOperatingSystemUpdate;
    if (isOs) hostsWithOsFailure.add(failure.hostId);

    items.add(HealthItem(
      key: 'failure-${failure.id}',
      kind: isOs ? HealthKind.osUpdate : HealthKind.application,
      hostId: failure.hostId,
      hostname: failure.hostname,
      operatingSystem: host?.operatingSystem,
      subject: failure.title,
      cause: failure.causeTitle ?? 'The upgrade script failed',
      explanation: failure.causeExplanation ??
          'The upgrade for ${failure.applicationName} ran on this host and failed.',
      suggestedFix: failure.suggestedFix ??
          'Open it in Failed Updates to read the output and repair the script.',
      tries: failure.failureCount,
      since: failure.firstFailedUtc,
      details: failure.details,
      osUpdatePending: isOs,
    ));
  }

  for (final host in hosts) {
    if (host.status != HostStatus.offline) continue;
    if (hostsWithOsFailure.contains(host.id)) continue;
    final waiting = host.operatingSystemUpdateAvailable == true || host.appUpdatesAvailableCount > 0;
    if (!waiting) continue;

    items.add(HealthItem(
      key: 'offline-${host.id}',
      kind: HealthKind.offline,
      hostId: host.id,
      hostname: host.hostname,
      operatingSystem: host.operatingSystem,
      subject: 'Pending updates',
      cause: 'Offline or asleep',
      explanation: 'The host has updates waiting and has not checked in, so nothing can be tried on it.',
      suggestedFix: 'Nothing to do: it patches when it comes back online. If it should be on, check the machine.',
      tries: 0,
      since: host.lastSeenUtc,
      details: null,
      osUpdatePending: host.operatingSystemUpdateAvailable == true,
    ));
  }

  items.sort((a, b) {
    final byKind = a.kind.index.compareTo(b.kind.index);
    if (byKind != 0) return byKind;
    final byTries = b.tries.compareTo(a.tries);
    if (byTries != 0) return byTries;
    final aSince = a.since;
    final bSince = b.since;
    if (aSince == null || bSince == null) return 0;
    return aSince.compareTo(bSince);
  });

  return items;
}

/// Hosts with at least one row — the headline number, since a host failing twice is one machine to
/// look at.
int hostsNeedingAttention(List<HealthItem> items) => items.map((i) => i.hostId).toSet().length;
