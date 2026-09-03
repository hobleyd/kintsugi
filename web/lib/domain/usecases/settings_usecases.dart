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
