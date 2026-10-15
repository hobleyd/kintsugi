import '../entities/enums.dart';
import '../entities/settings.dart';
import '../repositories/repositories.dart';

class GetAiAgentSettings {
  const GetAiAgentSettings(this._repository);

  final AiAgentSettingsRepository _repository;

  Future<AiAgentSettings> call() => _repository.read();
}

class UpdateAiAgentSettings {
  const UpdateAiAgentSettings(this._repository);

  final AiAgentSettingsRepository _repository;

  Future<AiAgentSettings> call(AiAgentSettingsUpdate update) => _repository.update(update);
}

/// Lists the models an Ollama endpoint is serving, so the model is a choice rather than a string
/// to get right by hand.
class GetOllamaModels {
  const GetOllamaModels(this._repository);

  final AiAgentSettingsRepository _repository;

  Future<List<String>> call(String baseUrl) => _repository.ollamaModels(baseUrl);
}

class CheckGooseCliStatus {
  const CheckGooseCliStatus(this._repository);

  final AiAgentSettingsRepository _repository;

  Future<GooseCliStatus> call(String? endpoint) => _repository.gooseCliStatus(endpoint);
}

class CheckClaudeAgentSdkStatus {
  const CheckClaudeAgentSdkStatus(this._repository);

  final AiAgentSettingsRepository _repository;

  Future<ClaudeAgentSdkStatus> call() => _repository.claudeAgentSdkStatus();
}

class GetAuthenticationSettings {
  const GetAuthenticationSettings(this._repository);

  final AuthenticationSettingsRepository _repository;

  Future<AuthenticationSettings> call() => _repository.read();
}

/// Saves the identity provider.
///
/// The server clears the OIDC handler's cached options as part of this, which is not optional:
/// without it the next sign-in would keep using whichever provider and secret were current when
/// the scheme was first exercised.
class UpdateAuthenticationSettings {
  const UpdateAuthenticationSettings(this._repository);

  final AuthenticationSettingsRepository _repository;

  Future<AuthenticationSettings> call({
    required AuthProvider provider,
    required String? clientId,
    required String? clientSecret,
    required String? authority,
    required String? tenantId,
    required String? hostedDomain,
    required bool isEnabled,
  }) =>
      _repository.update(
        provider: provider,
        clientId: clientId,
        clientSecret: clientSecret,
        authority: authority,
        tenantId: tenantId,
        hostedDomain: hostedDomain,
        isEnabled: isEnabled,
      );
}

class GetAuditSettings {
  const GetAuditSettings(this._repository);

  final AuditSettingsRepository _repository;

  Future<AuditSettings> call() => _repository.read();
}

class UpdateAuditSettings {
  const UpdateAuditSettings(this._repository);

  final AuditSettingsRepository _repository;

  Future<AuditSettings> call({
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
  }) =>
      _repository.update(
        provider: provider,
        isEnabled: isEnabled,
        endpoint: endpoint,
        region: region,
        clientId: clientId,
        secret: secret,
        clearSecret: clearSecret,
        tenantId: tenantId,
        projectId: projectId,
        logGroup: logGroup,
        dataCollectionRuleId: dataCollectionRuleId,
        stream: stream,
        index: index,
      );
}

class GetGitHubSettings {
  const GetGitHubSettings(this._repository);

  final GitHubSettingsRepository _repository;

  Future<GitHubSettings> call() => _repository.read();
}

class UpdateGitHubSettings {
  const UpdateGitHubSettings(this._repository);

  final GitHubSettingsRepository _repository;

  Future<GitHubSettings> call({
    required String? agentPackageRepository,
    required String? scriptApprovalRepository,
    required String? apiToken,
    required bool clearApiToken,
    required String? scriptApprovalToken,
    required bool clearScriptApprovalToken,
  }) =>
      _repository.update(
        agentPackageRepository: agentPackageRepository,
        scriptApprovalRepository: scriptApprovalRepository,
        apiToken: apiToken,
        clearApiToken: clearApiToken,
        scriptApprovalToken: scriptApprovalToken,
        clearScriptApprovalToken: clearScriptApprovalToken,
      );
}

class StartGitHubAppSetup {
  const StartGitHubAppSetup(this._repository);

  final GitHubSettingsRepository _repository;

  Future<void> call({required String? organization, required String? name}) =>
      _repository.startGitHubAppSetup(organization: organization, name: name);
}

class DisconnectGitHubApp {
  const DisconnectGitHubApp(this._repository);

  final GitHubSettingsRepository _repository;

  Future<GitHubSettings> call() => _repository.disconnectGitHubApp();
}

class GetVantaSettings {
  const GetVantaSettings(this._repository);

  final VantaSettingsRepository _repository;

  Future<VantaSettings> call() => _repository.read();
}

class UpdateVantaSettings {
  const UpdateVantaSettings(this._repository);

  final VantaSettingsRepository _repository;

  Future<VantaSettings> call({
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
  }) =>
      _repository.update(
        enabled: enabled,
        clientId: clientId,
        clientSecret: clientSecret,
        clearClientSecret: clearClientSecret,
        apiBaseUrl: apiBaseUrl,
        vulnerableComponentResourceId: vulnerableComponentResourceId,
        packageVulnerabilityResourceId: packageVulnerabilityResourceId,
        consoleBaseUrl: consoleBaseUrl,
        severity: severity,
        syncIntervalHours: syncIntervalHours,
      );
}

class GetVantaSyncStatus {
  const GetVantaSyncStatus(this._repository);

  final VantaSettingsRepository _repository;

  Future<VantaSyncStatus> call() => _repository.readSyncStatus();
}

class StartVantaSync {
  const StartVantaSync(this._repository);

  final VantaSettingsRepository _repository;

  Future<VantaSyncStatus> call() => _repository.startSync();
}

class GetPatchingPolicySettings {
  const GetPatchingPolicySettings(this._repository);

  final PatchingPolicySettingsRepository _repository;

  Future<PatchingPolicySettings> call() => _repository.read();
}

class UpdatePatchingPolicySettings {
  const UpdatePatchingPolicySettings(this._repository);

  final PatchingPolicySettingsRepository _repository;

  Future<PatchingPolicySettings> call(PatchingPolicySettings settings) =>
      _repository.update(settings);
}

/// Routed mode's operations, one class rather than eight: they are only ever used together, by
/// one bloc, and each is a straight pass-through to the repository.
class ManageAiRouting {
  const ManageAiRouting(this._repository);

  final AiRoutingRepository _repository;

  Future<AiRouting> read() => _repository.read();
  Future<AiCatalog> catalog({bool refresh = false}) => _repository.catalog(refresh: refresh);
  Future<AiConnection> saveConnection(AiConnectionDraft draft) => _repository.saveConnection(draft);
  Future<void> deleteConnection(String id) => _repository.deleteConnection(id);
  Future<AiConnectionTestResult> testConnection(String id, String model) => _repository.testConnection(id, model);
  Future<AiFeatureRoute> setRoute(AiFeature feature, String connectionId, String model) =>
      _repository.setRoute(feature, connectionId, model);
  Future<void> clearRoute(AiFeature feature) => _repository.clearRoute(feature);
  Future<AiAgentSettings> updateWebSearch(
          {required WebSearchBackend backend,
          required String? apiKey,
          required bool clearApiKey,
          required String? baseUrl}) =>
      _repository.updateWebSearch(backend: backend, apiKey: apiKey, clearApiKey: clearApiKey, baseUrl: baseUrl);
}
