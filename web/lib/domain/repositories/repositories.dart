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
import '../entities/remote_control_session.dart';
import '../entities/session.dart';
import '../entities/settings.dart';
import '../entities/upgrade_path.dart';
import '../entities/upgrade_script.dart';

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

abstract interface class HostRepository {
  Future<List<HostSummary>> list();

  /// Asks the fleet to remove a host. The agent is told to uninstall itself completely on its
  /// next check-in; the host record survives until it confirms.
  Future<void> requestRemoval(String id);
}

abstract interface class RemoteControlRepository {
  /// Asks a host's agent to put the consent dialog in front of whoever is sitting at it. Returns at
  /// once — the answer arrives through [session], which the screen polls.
  Future<RemoteControlSession> request(String hostId);

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

  Future<UpgradePathPrompt> prompt({required String applicationName, String? platform});

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
  Future<UpgradePathResult> signScript({required String applicationName, required String platform});
}

abstract interface class AgentPackageRepository {
  Future<ClientsView> view();

  /// Downloads whatever the upstream repository has that this server does not, points it at this
  /// server, and publishes it locally.
  Future<ClientsView> refresh();
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

  Future<UpgradeScriptsView> takeServerScript({
    required String applicationName,
    required String platform,
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
