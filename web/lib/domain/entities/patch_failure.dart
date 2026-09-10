import 'package:equatable/equatable.dart';

import 'enums.dart';

/// One (host, application) upgrade that ran on a managed host and failed. Mirrors
/// `PatchFailureDto`.
///
/// One row per (host, application) rather than per attempt: a broken script fails again every patch
/// cycle, so repeats fold into [failureCount] and move [lastFailedUtc] instead of piling up. See
/// `Kintsugi.Domain.Entities.PatchFailure`.
class PatchFailure extends Equatable {
  const PatchFailure({
    required this.id,
    required this.hostId,
    required this.hostname,
    required this.serialNumber,
    required this.applicationName,
    required this.platform,
    required this.installedVersion,
    required this.attemptedVersion,
    required this.details,
    required this.firstFailedUtc,
    required this.lastFailedUtc,
    required this.failureCount,
    required this.resolution,
    required this.resolvedUtc,
    required this.hasScript,
    required this.scriptSigned,
    required this.method,
    required this.canFix,
  });

  final String id;
  final String hostId;
  final String hostname;
  final String? serialNumber;
  final String applicationName;

  /// The upgrade path's platform bucket — an OS (`macOS`, `Windows`, `Linux`) or a package manager
  /// (`pm:Homebrew`, ...) — resolved server-side when the failure was reported. Null when nothing
  /// resolved, which is what makes [canFix] false.
  final String? platform;

  final String? installedVersion;
  final String? attemptedVersion;

  /// The failing command's exit status and captured output, truncated by the agent before sending.
  final String details;

  final DateTime firstFailedUtc;

  /// The date the screen shows and sorts on: whether the problem is still live.
  final DateTime lastFailedUtc;

  final int failureCount;
  final PatchFailureResolution resolution;
  final DateTime? resolvedUtc;

  final bool hasScript;
  final bool scriptSigned;
  final UpgradeMethod method;

  /// Whether the fix panel can be offered at all. Computed server-side, from a join this client
  /// cannot see — deriving it here would be a second copy free to disagree, exactly as with
  /// `UpgradePathSummary.statusKey`.
  final bool canFix;

  bool get isOutstanding => resolution == PatchFailureResolution.outstanding;

  /// Whether this is a one-off or something that has been failing on a schedule — which is most of
  /// what tells a blip from a broken script.
  bool get isRepeating => failureCount > 1;

  @override
  List<Object?> get props => [
        id,
        hostId,
        hostname,
        serialNumber,
        applicationName,
        platform,
        installedVersion,
        attemptedVersion,
        details,
        firstFailedUtc,
        lastFailedUtc,
        failureCount,
        resolution,
        resolvedUtc,
        hasScript,
        scriptSigned,
        method,
        canFix,
      ];
}
