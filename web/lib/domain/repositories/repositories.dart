/// The boundary between this app's own logic and the API.
///
/// Every interface here is narrow on purpose — one screen's worth, or one settings page's worth —
/// rather than one repository per layer or one god interface for the whole API. A BLoC that only
/// reads hosts should not be able to see the route that signs a script, and the four settings
/// screens genuinely have nothing to do with each other beyond both being settings.
///
/// The implementations live in `lib/data/repositories/`, and `lib/core/di/injection.dart` is the
/// only file that names one.
library;

import '../entities/agent_package.dart';
import '../entities/application.dart';
import '../entities/enums.dart';
import '../entities/host.dart';
import '../entities/patch_failure.dart';
import '../entities/remote_control_session.dart';
import '../entities/session.dart';
import '../entities/settings.dart';
import '../entities/upgrade_path.dart';
import '../entities/upgrade_script.dart';
import '../entities/vulnerability.dart';

abstract interface class SessionRepository {
  /// Reads `GET /api/session` — the anonymous bootstrap call.
  Future<Session> read();

  /// Hands off to the identity provider by navigating the whole page.
  ///
  /// Not a request: the response is a redirect to the provider's own origin, which a fetch cannot
  /// usefully follow. [returnPath] comes back as the address the browser lands on afterwards.
  void signIn({String? returnPath});

  /// Signs out of the cookie and of the provider, which is also a whole-page round trip.
  void signOut();
}

abstract interface class ServerInfoRepository {
  /// This server build's version, from `GET /api/admin/server`. Gated like every other
  /// browser-driven route, which is why it is not a field on [Session]: that bootstrap call is
  /// anonymous, and a build version should wait until the caller has signed in.
  Future<String> version();
}

abstract interface class HostRepository {
  Future<List<HostSummary>> list();

  /// Asks the fleet to remove a host. The agent is told to uninstall itself completely on its
  /// next check-in; the host record survives until it confirms.
  Future<void> requestRemoval(String id);
}

abstract interface class RemoteControlRepository {
  /// Opens a session of the given kind. A screen session asks the host's user first; a shell
  /// session asks nobody. Returns at once either way — the answer arrives through [session], which
  /// the screen polls.
  Future<RemoteControlSession> request(String hostId, RemoteControlSessionKind kind);

  /// One session's current state, or null if the server has never heard of it.
  Future<RemoteControlSession?> session(String id);

  /// Hangs up: closes both sockets and stops the agent capturing.
  Future<void> end(String id);

  /// Opens the media channel for a session whose consent has been granted.
  RemoteControlStream openStream(String sessionId);
}

abstract interface class ApplicationRepository {
  Future<ApplicationOverview> overview();
}

abstract interface class UpgradePathRepository {
  Future<RunStarted<UpgradePathScanStatus>> startScan();

  Future<UpgradePathScanStatus> scanStatus();

  Future<RunStarted<UpdateCheckStatus>> startUpdateCheck();

  Future<UpdateCheckStatus> updateCheckStatus();

  /// Re-runs one row's script in its `--update-version` mode, synchronously. No AI call.
  Future<UpdateCheckResult> checkUpdate({required String applicationName, required String platform});

  /// Starts an AI refresh for one application.
  Future<RunStarted<UpgradePathRefreshStatus>> startRefresh({
    required String applicationName,
    String? platform,
    String? instructions,
  });

  Future<UpgradePathRefreshStatus> refreshStatus(String applicationName);

  /// [patchFailureId] asks for a *repair* prompt instead: the same research prompt with a brief
  /// describing that failure and the current script appended, composed server-side. The Failed
  /// Updates screen passes it; the Applications screen does not.
  Future<UpgradePathPrompt> prompt({
    required String applicationName,
    String? platform,
    String? patchFailureId,
  });

  /// Saves an upgrade path directly, without going through the AI.
  ///
  /// Takes the raw JSON body rather than an entity because this route round-trips content an
  /// operator may have edited by hand: fields this client does not model must survive the trip
  /// rather than being silently dropped on the way through.
  Future<UpgradePathResult> save(Map<String, dynamic> body);

  /// Signs whatever script is *already stored* for this row, so an agent will run it.
  ///
  /// Deliberately takes no script content. Signing is the human review the whole trust model rests
  /// on, and a signature that covered text the client just supplied would not be a review of
  /// anything the fleet is going to execute.
  /// [patchFailureId] marks this signature as a *repair* driven from the Failed Updates screen,
  /// which clears the failures that script was causing. Omitted by the Applications screen.
  Future<UpgradePathResult> signScript({
    required String applicationName,
    required String platform,
    String? patchFailureId,
  });
}

/// The Failed Updates screen's data — reads `/api/admin/patch-failures`.
abstract interface class PatchFailureRepository {
  Future<List<PatchFailure>> list();

  /// Clears one failure by hand. Everything else closes itself when the host next reports that
  /// application patched successfully.
  Future<void> dismiss(String id);
}

abstract interface class AgentPackageRepository {
  Future<ClientsView> view();

  /// Downloads whatever the upstream repository has that this server does not, points it at this
  /// server, and publishes it locally.
  Future<ClientsView> refresh();

  /// The silent PowerShell installer for the published Windows build, for deployment through
  /// CrowdStrike.
  ///
  /// Fetched on demand rather than carried in [view]: the rendered script contains the current
  /// enrollment token, so it should reach the browser when somebody asks to see it and not on
  /// every load of a screen that is mostly about something else.
  Future<WindowsBootstrapScript> windowsBootstrapScript();
}

abstract interface class UpgradeScriptRepository {
  Future<UpgradeScriptsView> view();

  Future<UpgradeScriptsView> refresh();

  Future<UpgradeScriptsView> adopt({
    required String applicationName,
    required String platform,
    required String sha256,
    required String signerFingerprint,
  });

  /// Addressed by (bucket, content hash) — what the screen lists — rather than by row.
  Future<UpgradeScriptsView> takeServerScript({
    required String platform,
    required String sha256,
  });
}

abstract interface class AiAgentSettingsRepository {
  Future<AiAgentSettings> read();

  /// A blank API key means "keep whatever is stored" — the form was never given the real value,
  /// so it has nothing to send back unchanged.
  Future<AiAgentSettings> update(AiAgentSettingsUpdate update);

  Future<List<String>> ollamaModels(String baseUrl);

  Future<GooseCliStatus> gooseCliStatus(String? endpoint);

  /// Takes no endpoint: the Claude Agent SDK runs as a subprocess of the API, and the token the
  /// probe needs is the stored one, which this client has never been given.
  Future<ClaudeAgentSdkStatus> claudeAgentSdkStatus();
}

/// The editable half of the AI agent settings.
class AiAgentSettingsUpdate {
  const AiAgentSettingsUpdate({
    required this.provider,
    required this.apiKey,
    required this.baseUrl,
    required this.model,
    required this.isEnabled,
  });

  final AiProvider provider;
  final String? apiKey;
  final String? baseUrl;
  final String? model;
  final bool isEnabled;
}

abstract interface class AuthenticationSettingsRepository {
  Future<AuthenticationSettings> read();

  /// [clientSecret] blank means "keep the stored secret".
  Future<AuthenticationSettings> update({
    required AuthProvider provider,
    required String? clientId,
    required String? clientSecret,
    required String? authority,
    required String? tenantId,
    required String? hostedDomain,
    required bool isEnabled,
  });
}

abstract interface class AuditSettingsRepository {
  Future<AuditSettings> read();

  /// A blank [secret] means "keep the stored one" for the same provider — changing the provider
  /// drops it server-side regardless. [clearSecret] is how one is removed from a provider whose
  /// secret is optional, since blank cannot mean both.
  Future<AuditSettings> update({
    required AuditProvider provider,
    required bool isEnabled,
    required String? endpoint,
    required String? region,
    required String? clientId,
    required String? secret,
    required bool clearSecret,
    required String? tenantId,
    required String? projectId,
    required String? logGroup,
    required String? dataCollectionRuleId,
    required String? stream,
    required String? index,
  });
}

abstract interface class VantaSettingsRepository {
  Future<VantaSettings> read();

  /// A blank client secret means "keep the stored one"; [clearClientSecret] is how one is removed,
  /// since blank cannot mean both.
  Future<VantaSettings> update({
    required bool enabled,
    required String? clientId,
    required String? clientSecret,
    required bool clearClientSecret,
    required String? apiBaseUrl,
    required String? vulnerableComponentResourceId,
    required String? packageVulnerabilityResourceId,
    required String? consoleBaseUrl,
    required double? severity,
    required int? syncIntervalHours,
  });

  Future<VantaSyncStatus> readSyncStatus();

  /// Starts a sync and returns its opening status. The run itself happens in the background — the
  /// screen polls [readSyncStatus] for the outcome.
  Future<VantaSyncStatus> startSync();
}

/// The Vulnerabilities screen and the CPE mapping queue behind it.
abstract interface class VulnerabilityRepository {
  /// One page of the fleet's exposure, filtered and sorted as [query] asks.
  ///
  /// Everything in [VulnerabilityQuery] is applied on the server, including the sort: the page is
  /// cut after it, so ordering in the browser would order the page rather than the set.
  Future<VulnerabilityOverview> readOverview(VulnerabilityQuery query);

  Future<List<CpeMapping>> readMappings();

  /// Candidates from NVD's own CPE dictionary. A weak signal offered to a human rather than
  /// adopted — the live dictionary ranks Slackware Linux first for "slack".
  Future<List<CpeCandidate>> searchCpeDictionary(String keyword);

  /// Accepts a vendor and product. Throws if NVD's dictionary contains no such product: a typo
  /// here silently attributes another product's CVEs to this one.
  Future<void> confirmMapping({required String id, required String vendor, required String product});

  /// Accepts what each of these subjects already proposes, in one request.
  ///
  /// Not a loop over [confirmMapping]: that route re-checks the pair against NVD's dictionary,
  /// which is right for something a reviewer typed and, at five requests per thirty seconds, fatal
  /// for forty ticked rows. The server skips the check for stored suggestions, which were checked
  /// before they were written, and reports what it did not do.
  Future<BulkMappingResult> confirmMappings(List<String> ids);

  Future<void> markMappingNotApplicable({required String id, String? notes});

  Future<void> resetMapping(String id);

  /// Returns several subjects to the queue at once, discarding what each was matched against.
  Future<BulkMappingResult> resetMappings(List<String> ids);

  Future<VulnerabilityRunStatus> readRunStatus();

  /// Starts an assessment and returns its opening status. The run happens in the background — the
  /// screen polls [readRunStatus] for the outcome.
  Future<VulnerabilityRunStatus> startRun();

  /// Asks the run in flight to stop, and returns the status that answer came with.
  ///
  /// Safe to call: every stage of a run commits as it goes, so a cancelled run keeps everything it
  /// had already assessed and the queue resumes from there. The status comes back with `running`
  /// still true and `cancelling` set — the run is inside an HTTP call to NVD or OSV and stops when
  /// it notices.
  Future<VulnerabilityRunStatus> cancelRun();
}

abstract interface class VulnerabilitySettingsRepository {
  Future<VulnerabilitySettings> read();

  /// A blank NVD API key means "keep the stored one"; [clearNvdApiKey] is how one is removed,
  /// since blank cannot mean both.
  Future<VulnerabilitySettings> update({
    required bool enabled,
    required String? nvdApiKey,
    required bool clearNvdApiKey,
    required int? syncIntervalHours,
    required int? assessmentsPerRun,
    required int? packagesPerRun,
    required bool? autoSuggestCpes,
  });
}

abstract interface class GitHubSettingsRepository {
  Future<GitHubSettings> read();

  /// A blank token means "keep the stored one"; the matching `clear` flag is how one is removed,
  /// since blank cannot mean both.
  Future<GitHubSettings> update({
    required String? agentPackageRepository,
    required String? scriptApprovalRepository,
    required String? apiToken,
    required bool clearApiToken,
    required String? scriptApprovalToken,
    required bool clearScriptApprovalToken,
  });
}

abstract interface class PatchingPolicySettingsRepository {
  Future<PatchingPolicySettings> read();

  Future<PatchingPolicySettings> update(PatchingPolicySettings settings);
}
