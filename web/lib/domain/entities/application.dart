import 'package:equatable/equatable.dart';

import 'upgrade_path.dart';

/// Everything the Applications screen renders. Mirrors `ApplicationOverviewDto`.
class ApplicationOverview extends Equatable {
  const ApplicationOverview({
    required this.applications,
    required this.totalApplicationCount,
    required this.allHostNames,
  });

  const ApplicationOverview.empty()
      : applications = const [],
        totalApplicationCount = 0,
        allHostNames = const [];

  final List<ApplicationRow> applications;

  /// Every distinct application reported, children included — which is why it is not
  /// `applications.length`.
  final int totalApplicationCount;

  final List<String> allHostNames;

  @override
  List<Object?> get props => [applications, totalApplicationCount, allHostNames];
}

/// One application, with the upgrade paths researched for it and any package-manager-managed
/// applications nested underneath. Mirrors `ApplicationRowDto`.
class ApplicationRow extends Equatable {
  const ApplicationRow({
    required this.name,
    required this.hostCount,
    required this.hostNames,
    required this.upgradePaths,
    required this.children,
  });

  final String name;
  final int hostCount;
  final List<String> hostNames;

  /// One per platform this application is installed on. Empty means nothing has been researched
  /// for it yet.
  final List<UpgradePathSummary> upgradePaths;

  /// Applications a package manager owns, listed under the manager rather than beside it — a
  /// Homebrew cask under Homebrew, for instance.
  final List<ApplicationRow> children;

  @override
  List<Object?> get props => [name, hostCount, hostNames, upgradePaths, children];
}

/// What "Patch now" did. Mirrors `RequestForcedPatchRunsResult`.
///
/// Carries the hosts it could *not* reach rather than only a count, because that is the half an
/// operator acting on an emergency has to see: a filter resolved in the browser can name a host the
/// server has since removed, and "12 hosts told" would hide that the one machine they were looking
/// at is not among them.
class ForcedPatchRunResult extends Equatable {
  const ForcedPatchRunResult({required this.requested, required this.notRequested});

  final int requested;
  final List<String> notRequested;

  @override
  List<Object?> get props => [requested, notRequested];
}
