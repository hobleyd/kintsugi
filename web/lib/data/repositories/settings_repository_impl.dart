import '../../core/network/api_client.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';
import '../../domain/repositories/repositories.dart';
import '../models/settings_mapper.dart';

/// The AI agent's settings, which are the one set that already had a REST surface of its own —
/// `/api/ai-settings`, gated at the class level — so this points at that rather than at
/// `/api/admin/settings`.
class AiAgentSettingsRepositoryImpl implements AiAgentSettingsRepository {
  const AiAgentSettingsRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<AiAgentSettings> read() async =>
      aiAgentSettingsFromJson(await _api.getJson('/api/ai-settings') as Map<String, dynamic>);

  @override
  Future<AiAgentSettings> update(AiAgentSettingsUpdate update) async => aiAgentSettingsFromJson(
        await _api.putJson('/api/ai-settings', body: {
          // An ordinal, not a name: AiProvider carries no JSON converter, so System.Text.Json
          // reads it as a number. See lib/core/network/json_reader.dart for why that asymmetry is
          // not ours to tidy up.
          'provider': update.provider.index,
          'apiKey': update.apiKey,
          'baseUrl': update.baseUrl,
          'model': update.model,
          'isEnabled': update.isEnabled,
        }) as Map<String, dynamic>,
      );

  @override
  Future<List<String>> ollamaModels(String baseUrl) async {
    final json = await _api.getJson('/api/ai-settings/ollama-models', query: {'baseUrl': baseUrl});
    return json is List ? json.map((e) => e.toString()).toList() : const [];
  }

  @override
  Future<GooseCliStatus> gooseCliStatus(String? endpoint) async => gooseCliStatusFromJson(
        await _api.getJson('/api/ai-settings/goose-cli-status', query: {'endpoint': endpoint})
            as Map<String, dynamic>,
      );

  @override
  Future<ClaudeAgentSdkStatus> claudeAgentSdkStatus() async => claudeAgentSdkStatusFromJson(
        await _api.getJson('/api/ai-settings/claude-agent-sdk-status') as Map<String, dynamic>,
      );
}

class AuthenticationSettingsRepositoryImpl implements AuthenticationSettingsRepository {
  const AuthenticationSettingsRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<AuthenticationSettings> read() async => authenticationSettingsFromJson(
        await _api.getJson('/api/admin/settings/authentication') as Map<String, dynamic>,
      );

  @override
  Future<AuthenticationSettings> update({
    required AuthProvider provider,
    required String? clientId,
    required String? clientSecret,
    required String? authority,
    required String? tenantId,
    required String? hostedDomain,
    required bool isEnabled,
  }) async =>
      authenticationSettingsFromJson(
        await _api.putJson('/api/admin/settings/authentication', body: {
          'provider': provider.index,
          'clientId': clientId,
          'clientSecret': clientSecret,
          'authority': authority,
          'tenantId': tenantId,
          'hostedDomain': hostedDomain,
          'isEnabled': isEnabled,
        }) as Map<String, dynamic>,
      );
}

class GitHubSettingsRepositoryImpl implements GitHubSettingsRepository {
  const GitHubSettingsRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<GitHubSettings> read() async => gitHubSettingsFromJson(
        await _api.getJson('/api/admin/settings/github') as Map<String, dynamic>,
      );

  @override
  Future<GitHubSettings> update({
    required String? agentPackageRepository,
    required String? scriptApprovalRepository,
    required String? apiToken,
    required bool clearApiToken,
    required String? scriptApprovalToken,
    required bool clearScriptApprovalToken,
  }) async =>
      gitHubSettingsFromJson(
        await _api.putJson('/api/admin/settings/github', body: {
          'agentPackageRepository': agentPackageRepository,
          'scriptApprovalRepository': scriptApprovalRepository,
          'apiToken': apiToken,
          'clearApiToken': clearApiToken,
          'scriptApprovalToken': scriptApprovalToken,
          'clearScriptApprovalToken': clearScriptApprovalToken,
        }) as Map<String, dynamic>,
      );
}

class VantaSettingsRepositoryImpl implements VantaSettingsRepository {
  const VantaSettingsRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<VantaSettings> read() async => vantaSettingsFromJson(
        await _api.getJson('/api/admin/settings/vanta') as Map<String, dynamic>,
      );

  @override
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
  }) async =>
      vantaSettingsFromJson(
        await _api.putJson('/api/admin/settings/vanta', body: {
          'enabled': enabled,
          'clientId': clientId,
          'clientSecret': clientSecret,
          'clearClientSecret': clearClientSecret,
          'apiBaseUrl': apiBaseUrl,
          'vulnerableComponentResourceId': vulnerableComponentResourceId,
          'packageVulnerabilityResourceId': packageVulnerabilityResourceId,
          'consoleBaseUrl': consoleBaseUrl,
          'severity': severity,
          'syncIntervalHours': syncIntervalHours,
        }) as Map<String, dynamic>,
      );

  @override
  Future<VantaSyncStatus> readSyncStatus() async => vantaSyncStatusFromJson(
        await _api.getJson('/api/admin/settings/vanta/sync-status') as Map<String, dynamic>,
      );

  @override
  Future<VantaSyncStatus> startSync() async => vantaSyncStatusFromJson(
        await _api.postJson('/api/admin/settings/vanta/sync') as Map<String, dynamic>,
      );
}

class PatchingPolicySettingsRepositoryImpl implements PatchingPolicySettingsRepository {
  const PatchingPolicySettingsRepositoryImpl(this._api);

  final ApiClient _api;

  /// Reads from `/api/admin/settings/patching-policy`, not the `/api/patching-policy` the agents
  /// poll. Same data; that path is inside nginx's agent regex and needs a client certificate.
  @override
  Future<PatchingPolicySettings> read() async => patchingPolicyFromJson(
        await _api.getJson('/api/admin/settings/patching-policy') as Map<String, dynamic>,
      );

  @override
  Future<PatchingPolicySettings> update(PatchingPolicySettings settings) async =>
      patchingPolicyFromJson(
        await _api.putJson('/api/admin/settings/patching-policy', body: {
          'intervalValue': settings.intervalValue,
          // Ordinals, and this pair is the clearest case of why: all three Rust agents read
          // `interval_unit` off the policy as a `u8` (see clients/*/src/policy.rs).
          'intervalUnit': settings.intervalUnit.index,
          'delayValue': settings.delayValue,
          'delayUnit': settings.delayUnit.index,
          'maxDelayCount': settings.maxDelayCount,
        }) as Map<String, dynamic>,
      );
}
