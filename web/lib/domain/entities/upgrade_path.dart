import 'package:equatable/equatable.dart';

import 'enums.dart';

/// One researched (application, platform) upgrade path. Mirrors `UpgradePathSummaryDto`.
class UpgradePathSummary extends Equatable {
  const UpgradePathSummary({
    required this.applicationName,
    required this.platform,
    required this.status,
    required this.statusKey,
    required this.latestVersion,
    required this.method,
    required this.downloadUrl,
    required this.command,
    required this.instructions,
    required this.sourceUrl,
    required this.notes,
    required this.checkedUtc,
    required this.hostCount,
    required this.upToDateHostCount,
    required this.updateAvailableHostCount,
    required this.hostNames,
    required this.hostNamesNeedingUpdate,
    required this.script,
    required this.scriptSignature,
  });

  final String applicationName;

  /// The platform bucket: an OS (`macOS`, `Windows`, `Linux`) for an AI-researched row, or a
  /// package manager (`pm:Homebrew`, `pm:winget`, ...) for one a manager owns.
  final String platform;

  final UpgradePathStatus status;

  /// The one status this row displays as, computed server-side.
  ///
  /// Taken from the response rather than derived here on purpose: the precedence is not obvious
  /// (an unsigned script outranks "update available", because an unsigned script is one no agent
  /// will run at all) and the same value drives the status filter. Deriving it again here would be
  /// a second copy free to disagree with the server's.
  final String statusKey;

  final String? latestVersion;
  final UpgradeMethod method;
  final String? downloadUrl;
  final String? command;
  final String? instructions;
  final String? sourceUrl;
  final String? notes;
  final DateTime checkedUtc;
  final int hostCount;
  final int upToDateHostCount;
  final int updateAvailableHostCount;

  /// Which hosts resolved to *this* bucket.
  ///
  /// Distinct from `ApplicationRow.hostNames`, which is keyed on the application's name alone: an
  /// application installed from Homebrew on a Mac and from winget on a PC is two rows here sharing
  /// one application-level host list, so filtering the table on that list kept the `pm:Homebrew`
  /// row on screen when a Windows host was chosen.
  ///
  /// Empty means the field was absent — the server always names at least one host for a row it
  /// emits at all — which is what a bundle older or newer than the API it is talking to looks
  /// like. `ApplicationFilters.matches` falls back to the application's own list for that reason.
  final List<String> hostNames;

  /// Which of those hosts are behind on *this* application specifically.
  ///
  /// Distinct from [hostNames] as well, and the distinction matters when the host and status
  /// filters are combined: "Update Available" is fleet-wide (true if any host anywhere is behind),
  /// so filtering on installation alone would surface applications the chosen host is already
  /// current on just because some other host is not.
  final List<String> hostNamesNeedingUpdate;

  final String? script;

  /// Present once a human has signed the script. Until then no agent will run it — each agent
  /// verifies against the key it pinned at enrollment — so an unsigned script is inert rather
  /// than trusted.
  final String? scriptSignature;

  bool get isSigned => scriptSignature != null;

  @override
  List<Object?> get props => [
        applicationName,
        platform,
        status,
        statusKey,
        latestVersion,
        method,
        downloadUrl,
        command,
        instructions,
        sourceUrl,
        notes,
        checkedUtc,
        hostCount,
        upToDateHostCount,
        updateAvailableHostCount,
        hostNames,
        hostNamesNeedingUpdate,
        script,
        scriptSignature,
      ];
}

/// The result of researching or saving one upgrade path. Mirrors `UpgradePathResultDto`.
class UpgradePathResult extends Equatable {
  const UpgradePathResult({
    required this.applicationName,
    required this.platform,
    required this.status,
    required this.latestVersion,
    required this.method,
    required this.downloadUrl,
    required this.command,
    required this.instructions,
    required this.sourceUrl,
    required this.notes,
    required this.checkedUtc,
    required this.script,
    required this.scriptSigned,
    required this.approvalOutcome,
    required this.approvalPullRequestUrl,
    required this.approvalMessage,
    required this.raw,
  });

  final String applicationName;
  final String platform;
  final UpgradePathStatus status;
  final String? latestVersion;
  final UpgradeMethod method;
  final String? downloadUrl;
  final String? command;
  final String? instructions;
  final String? sourceUrl;
  final String? notes;
  final DateTime checkedUtc;
  final String? script;
  final bool scriptSigned;

  /// What happened when the signature was published upstream. Signing always stores the local
  /// signature regardless (see `SignUpgradePathScriptCommandHandler`), so this only ever explains
  /// the publishing half and never contradicts "signed".
  final ScriptApprovalPublishOutcome? approvalOutcome;

  final String? approvalPullRequestUrl;
  final String? approvalMessage;

  /// The decoded JSON this was read from.
  ///
  /// Kept because the Applications screen's editor round-trips a whole result: a result shown
  /// there can be edited and posted back to `/api/upgrade-paths/save`, and the fields this entity
  /// does not model must survive that trip rather than being dropped on the way through. It is
  /// also what the editor displays when the method is not `script` and there is no single field
  /// that is obviously "the" content.
  final Map<String, dynamic> raw;

  bool get hasScript => method == UpgradeMethod.script && (script?.isNotEmpty ?? false);

  /// A script is signable only once it is actually present — a bare "found" with no script has
  /// nothing to sign — and only while it is not already signed, so re-signing an untouched
  /// approved script is not offered as though there were something new to review.
  bool get isSignable => hasScript && !scriptSigned;

  @override
  List<Object?> get props => [applicationName, platform, status, method, script, scriptSigned, raw];
}

/// The AI instructions for one application, and whatever result is already stored.
/// Mirrors `UpgradePathPromptDto`.
class UpgradePathPrompt extends Equatable {
  const UpgradePathPrompt({
    required this.available,
    required this.platform,
    required this.prompt,
    required this.reason,
    required this.existingResult,
  });

  /// False when no AI research applies — the application is not installed anywhere, a package
  /// manager owns it, or no AI agent is configured. [reason] says which.
  final bool available;

  final String? platform;
  final String? prompt;
  final String? reason;
  final UpgradePathResult? existingResult;

  @override
  List<Object?> get props => [available, platform, prompt, reason, existingResult];
}

/// Progress of the fleet-wide "Find Upgrade Paths" run. Mirrors `UpgradePathScanStatusDto`.
class UpgradePathScanStatus extends Equatable {
  const UpgradePathScanStatus({
    required this.isRunning,
    required this.total,
    required this.completed,
    required this.resolved,
    required this.notFound,
    required this.failed,
    required this.skipped,
    required this.startedUtc,
    required this.completedUtc,
    required this.faultReason,
    required this.notes,
  });

  const UpgradePathScanStatus.idle()
      : isRunning = false,
        total = 0,
        completed = 0,
        resolved = 0,
        notFound = 0,
        failed = 0,
        skipped = 0,
        startedUtc = null,
        completedUtc = null,
        faultReason = null,
        notes = const [];

  final bool isRunning;
  final int total;
  final int completed;
  final int resolved;
  final int notFound;
  final int failed;
  final int skipped;
  final DateTime? startedUtc;
  final DateTime? completedUtc;
  final String? faultReason;

  /// Things the run wants a human to look at. Listed rather than counted — that is the point of
  /// them.
  final List<String> notes;

  double get fraction => total > 0 ? completed / total : 0;

  @override
  List<Object?> get props =>
      [isRunning, total, completed, resolved, notFound, failed, skipped, startedUtc, completedUtc, faultReason, notes];
}

/// Progress of "Check for Updates". Mirrors `UpdateCheckStatusDto`.
///
/// A separate type from the scan above rather than a shared shape: this run re-executes each
/// resolved script's own `--update-version` and makes no AI call, so its outcome counts are
/// different ones (updated/unchanged/failed, not resolved/not-found/skipped).
class UpdateCheckStatus extends Equatable {
  const UpdateCheckStatus({
    required this.isRunning,
    required this.total,
    required this.completed,
    required this.updated,
    required this.unchanged,
    required this.failed,
    required this.startedUtc,
    required this.completedUtc,
    required this.faultReason,
  });

  const UpdateCheckStatus.idle()
      : isRunning = false,
        total = 0,
        completed = 0,
        updated = 0,
        unchanged = 0,
        failed = 0,
        startedUtc = null,
        completedUtc = null,
        faultReason = null;

  final bool isRunning;
  final int total;
  final int completed;
  final int updated;
  final int unchanged;
  final int failed;
  final DateTime? startedUtc;
  final DateTime? completedUtc;
  final String? faultReason;

  double get fraction => total > 0 ? completed / total : 0;

  @override
  List<Object?> get props =>
      [isRunning, total, completed, updated, unchanged, failed, startedUtc, completedUtc, faultReason];
}

/// Progress of a single application's AI refresh. Mirrors `UpgradePathRefreshStatusDto`.
class UpgradePathRefreshStatus extends Equatable {
  const UpgradePathRefreshStatus({
    required this.isRunning,
    required this.startedUtc,
    required this.completedUtc,
    required this.success,
    required this.errorMessage,
    required this.results,
  });

  const UpgradePathRefreshStatus.idle()
      : isRunning = false,
        startedUtc = null,
        completedUtc = null,
        success = null,
        errorMessage = null,
        results = const [];

  final bool isRunning;

  /// The server's own record of when the run began, used rather than "now" so elapsed time stays
  /// accurate when a panel is reopened onto a run that was already going.
  final DateTime? startedUtc;

  final DateTime? completedUtc;

  /// Null until a run has finished. `false` means the run completed and failed, which is a
  /// different thing from "no run has happened", and the panel says so differently.
  final bool? success;

  final String? errorMessage;
  final List<UpgradePathResult> results;

  /// The result for [platform], or the first one when nothing matches.
  ///
  /// A refresh can cover more than one platform for the same application, and a panel is opened
  /// against one row. Falling back to the first rather than to nothing keeps a failed run's
  /// explanation visible instead of leaving the box looking empty.
  UpgradePathResult? resultFor(String? platform) {
    if (results.isEmpty) return null;
    if (platform != null) {
      for (final result in results) {
        if (result.platform == platform) return result;
      }
    }
    return results.first;
  }

  @override
  List<Object?> get props => [isRunning, startedUtc, completedUtc, success, errorMessage, results];
}

/// Whether starting a background run actually started one.
class RunStarted<T> extends Equatable {
  const RunStarted({required this.started, required this.status});

  /// False when a run was already going — in which case [status] is that run's progress, and the
  /// screen shows it rather than reporting a failure.
  final bool started;

  final T status;

  @override
  List<Object?> get props => [started, status];
}
