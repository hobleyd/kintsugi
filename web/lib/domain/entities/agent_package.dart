import 'package:equatable/equatable.dart';

import 'enums.dart';

/// An installable kintsugi-agent package published on this server. Mirrors `AgentPackageDto`.
class AgentPackage extends Equatable {
  const AgentPackage({
    required this.platform,
    required this.version,
    required this.fileName,
    required this.fileSizeBytes,
    required this.sha256,
    required this.releaseNotes,
    required this.publishedUtc,
  });

  /// `macos`, `windows` or `linux` — the agent-package namespace, which is deliberately *not*
  /// `PlatformBucket`'s (`macOS`, `Windows`, `pm:Homebrew`, ...). They name different things.
  final String platform;

  final String version;
  final String fileName;
  final int fileSizeBytes;
  final String sha256;
  final String? releaseNotes;
  final DateTime publishedUtc;

  @override
  List<Object?> get props => [platform, version, fileName, fileSizeBytes, sha256, releaseNotes, publishedUtc];
}

/// What the upstream repository currently offers. Mirrors `AgentPackageSourceStatusDto`.
class AgentPackageSourceStatus extends Equatable {
  const AgentPackageSourceStatus({
    required this.sourceDescription,
    required this.platforms,
    required this.unavailableReason,
  });

  const AgentPackageSourceStatus.unknown()
      : sourceDescription = '',
        platforms = const [],
        unavailableReason = null;

  final String sourceDescription;
  final List<AgentPackageSourceRow> platforms;

  /// Set when the upstream repository could not be read. Reported as a note beside a working
  /// screen rather than as an error in place of one: whatever is already published here is
  /// installable whether or not GitHub is reachable.
  final String? unavailableReason;

  bool get hasNewVersions => platforms.any((p) => p.isNewer);

  AgentPackageSourceRow? rowFor(String platform) {
    for (final row in platforms) {
      if (row.platform.toLowerCase() == platform.toLowerCase()) return row;
    }
    return null;
  }

  @override
  List<Object?> get props => [sourceDescription, platforms, unavailableReason];
}

/// One platform's standing against the upstream repository. Mirrors `AgentPackageSourceStatusRow`.
class AgentPackageSourceRow extends Equatable {
  const AgentPackageSourceRow({
    required this.platform,
    required this.availableVersion,
    required this.publishedVersion,
    required this.isNewer,
    required this.newerReleases,
  });

  final String platform;
  final String availableVersion;

  /// Null when this platform has nothing published here yet, which reads differently from being
  /// behind a version.
  final String? publishedVersion;

  final bool isNewer;

  /// Every upstream build newer than [publishedVersion], highest first — what the row's expander
  /// lists. Empty when the platform is up to date, so an expanded up-to-date row says so rather
  /// than showing nothing.
  final List<AgentPackageReleaseNotes> newerReleases;

  @override
  List<Object?> get props => [platform, availableVersion, publishedVersion, isNewer, newerReleases];
}

/// One upstream build's release notes. Mirrors `AgentPackageReleaseNotesDto`.
class AgentPackageReleaseNotes extends Equatable {
  const AgentPackageReleaseNotes({required this.version, required this.releaseNotes});

  final String version;

  /// Null when the GitHub release has an empty body — shown as such, because "no notes were
  /// written" is information about the build and a blank panel is not.
  final String? releaseNotes;

  @override
  List<Object?> get props => [version, releaseNotes];
}

/// What happened to one platform during a refresh. Mirrors `AgentPackageImportResultDto`.
class AgentPackageImportResult extends Equatable {
  const AgentPackageImportResult({
    required this.platform,
    required this.version,
    required this.outcome,
    required this.message,
  });

  final String platform;
  final String version;
  final AgentPackageImportOutcome outcome;
  final String? message;

  @override
  List<Object?> get props => [platform, version, outcome, message];
}

/// The Clients screen's whole state. Mirrors `ClientsViewDto`.
class ClientsView extends Equatable {
  const ClientsView({
    required this.packages,
    required this.sourceStatus,
    required this.agentApiBaseUrl,
    required this.agentApiBaseUrlIsDerived,
    required this.requestBaseUrl,
    required this.importResults,
    required this.refreshError,
  });

  final List<AgentPackage> packages;
  final AgentPackageSourceStatus sourceStatus;

  /// The address baked into each imported package's bundled `config.toml`.
  final String agentApiBaseUrl;

  /// True when `AGENT_API_BASE_URL` is unset and the address above was guessed from the request.
  ///
  /// Shown loudly, because getting it wrong fails in the quietest way this system has: nginx is
  /// what verifies an agent's client certificate, so any TLS-terminating hop in front of it ends
  /// the handshake at itself — and `/api/host/enroll` sits outside nginx's certificate regex, so
  /// the agent enrolls, looks installed, and then 403s on every authenticated route forever.
  final bool agentApiBaseUrlIsDerived;

  final String requestBaseUrl;
  final List<AgentPackageImportResult> importResults;
  final String? refreshError;

  @override
  List<Object?> get props => [
        packages,
        sourceStatus,
        agentApiBaseUrl,
        agentApiBaseUrlIsDerived,
        requestBaseUrl,
        importResults,
        refreshError,
      ];
}
