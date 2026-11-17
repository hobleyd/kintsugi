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

import 'dart:typed_data';

import '../entities/agent_package.dart';
import '../entities/ai_usage.dart';
import '../entities/application.dart';
import '../entities/autonomy.dart';
import '../entities/device_report.dart';
import '../entities/baseline.dart';
import '../entities/enums.dart';
import '../entities/host.dart';
import '../entities/managed_application.dart';
import '../entities/host_activity.dart';
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

/// The applications another tool patches — `/api/admin/managed-applications`. Every call answers with
/// the whole state.
abstract interface class ManagedApplicationRepository {
  Future<ManagedApplications> read();

  /// Says that [owner] patches each of [names]. An owner not on the list is added to it.
  Future<ManagedApplications> mark({required List<String> names, required String owner});

  Future<ManagedApplications> unmark(String applicationName);

  Future<ManagedApplications> addOwner(String name);

  /// Refused by the server while an application is still managed by [name].
  Future<ManagedApplications> removeOwner(String name);
}

abstract interface class ApplicationRepository {
  Future<ApplicationOverview> overview();

  /// Tells [hostNames] to run [applicationName]'s upgrade script at their next opportunity, rather
  /// than waiting for their own patching schedule.
  ///
  /// The host list is resolved here rather than server-side because the Applications screen's
  /// filters are client-side — the set the operator is looking at exists only in the browser. See
  /// `RequestForcedPatchRunsCommand`.
  Future<ForcedPatchRunResult> forcePatchRuns({
    required String applicationName,
    required String platform,
    required List<String> hostNames,
  });
}

abstract interface class UpgradePathRepository {
  Future<RunStarted<UpgradePathScanStatus>> startScan();

  Future<UpgradePathScanStatus> scanStatus();

  Future<RunStarted<UpdateCheckStatus>> startUpdateCheck();

  Future<UpdateCheckStatus> updateCheckStatus();

  /// A row per application the last "Find Upgrade Paths" run reported on, with its outcome. Read once
  /// when the run finishes, not polled with the status.
  Future<RunResults> scanResults();

  /// The same for the last "Check for Updates" run.
  Future<RunResults> updateCheckResults();

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
    required String scriptSha256,
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

/// The host activity page's data — reads `/api/admin/host-activity`.
abstract interface class HostActivityRepository {
  Future<HostActivity> get(String hostId);

  /// Returns the retention the server stored.
  Future<int> setRetention(int months);
}

/// The Autonomy page's data — reads `/api/admin/autonomy`.
abstract interface class AutonomyRepository {
  Future<Autonomy> get(AutonomyPeriod period, {String? month});

  Future<AutonomyDevicesPage> devices(String key, {int skip = 0, int take = 25, String? search});

  Future<AutonomyPatchNowResult> patchNow(String key, {required int expectedConnected, required bool includeOffline});
}

/// One device's report and its collected logs — reads and writes `/api/admin/device-report`.
abstract interface class DeviceReportRepository {
  Future<DeviceReport> get(String hostId, {int days = 7});

  /// The newest collection without its content, or null when there is none (the server answers 204).
  Future<LogCollection?> latestLogs(String hostId);

  Future<LogCollection> requestLogs(String hostId, {required int periodDays, required LogDetail detail});

  /// Deletes a collected log, or cancels a request nobody has picked up.
  Future<void> deleteLogs(String hostId, String id);

  Future<LogEntriesPage> entries(
    String hostId,
    String id, {
    LogSeverityFilter severity,
    String? search,
    DateTime? around,
    int aroundMinutes,
    int skip,
    int take,
  });

  Future<Uint8List> downloadLogs(String hostId, String id);

  /// Devices whose name or serial number contains [text], for opening any device's report.
  Future<List<DeviceSearchResult>> search(String text, {int take = 10});
}

/// The Baselines screens — reads and writes `/api/admin/baselines`. Nothing here writes to a
/// device, and nothing reaches one until a person signs a version.
abstract interface class BaselineRepository {
  Future<List<BaselineSummary>> list();

  Future<BaselineDetail> get(String baselineId);

  Future<BaselineHostDetail> host(String baselineId, String hostId);

  /// Creates a baseline from a `{name, items}` body — the shape of `baselines/master-daily.json`.
  Future<void> create(Map<String, dynamic> body);

  /// Adds an unsigned version from an `{items}` body (a `{name, items}` file works too; the name is ignored).
  Future<void> addVersion(String baselineId, Map<String, dynamic> body);

  /// Signs one version. [documentSha256] is the hash of the document text the reviewer was shown.
  Future<void> sign(String versionId, String documentSha256);

  Future<void> assign(String baselineId, {required bool allWindowsHosts, required List<String> hostNames});
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

/// The AI usage ledger's totals and the monthly budget — `/api/ai-settings/usage` and `/budget`.
abstract interface class AiUsageRepository {
  Future<AiUsage> read();

  Future<AiBudgetSettings> saveBudget(AiBudgetSettings settings);
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

/// Routed mode's half of the AI Agent screen: connections, per-feature routes, web search and the
/// models.dev catalog.
abstract interface class AiRoutingRepository {
  Future<AiRouting> read();

  Future<AiCatalog> catalog({bool refresh = false});

  Future<AiConnection> saveConnection(AiConnectionDraft draft);

  Future<void> deleteConnection(String id);

  Future<AiConnectionTestResult> testConnection(String id, String model);

  Future<AiFeatureRoute> setRoute(AiFeature feature, String connectionId, String model);

  Future<void> clearRoute(AiFeature feature);

  /// A null [apiKey] keeps the stored key; [clearApiKey] removes it.
  Future<AiAgentSettings> updateWebSearch(
      {required WebSearchBackend backend, required String? apiKey, required bool clearApiKey, required String? baseUrl});
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

  /// [clientSecret] blank means "keep the stored secret". [adminEmails] null leaves the stored list
  /// untouched (`UpdateAuthenticationSettingsCommand`); an empty list clears it, which the server
  /// reads as "every signed-in user".
  Future<AuthenticationSettings> update({
    required AuthProvider provider,
    required String? clientId,
    required String? clientSecret,
    required String? authority,
    required String? tenantId,
    required String? hostedDomain,
    required bool isEnabled,
    required List<String>? adminEmails,
  });
}

/// The administrators list as the Administrators page edits it — `/api/admin/settings/administrators`.
/// Every call answers with the list as it now stands, so the page never has to guess what changed.
abstract interface class AdministratorsRepository {
  Future<List<Administrator>> list();

  /// Adds each address, recorded as added by the signed-in caller (the server decides who that is).
  Future<List<Administrator>> add(List<String> emails);

  /// Takes one address off the list. The server refuses one that would leave a non-empty list
  /// without the caller while sign-in is required.
  Future<List<Administrator>> remove(String email);
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

  /// Fetches a manifest for [organization] (blank for the administrator's own account) and
  /// navigates the whole page to GitHub with it — GitHub has the administrator confirm the App
  /// before creating it, so this leaves the app, and comes back through the server's callback.
  Future<void> startGitHubAppSetup({required String? organization, required String? name});

  /// Forgets the connected App; the stored tokens apply again.
  Future<GitHubSettings> disconnectGitHubApp();
}

abstract interface class PatchingPolicySettingsRepository {
  Future<PatchingPolicySettings> read();

  Future<PatchingPolicySettings> update(PatchingPolicySettings settings);
}
