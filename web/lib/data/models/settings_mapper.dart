import '../../core/network/json_reader.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';

const _aiProviderNames = ['Anthropic', 'OpenAI', 'Ollama', 'GooseCli'];
const _authProviderNames = ['GoogleWorkspace', 'MicrosoftEntra', 'GenericOidc', 'Clerk'];
const _timeUnitNames = ['Hours', 'Days'];

/// Reads an `AiAgentSettingsDto`.
AiAgentSettings aiAgentSettingsFromJson(Map<String, dynamic> json) => AiAgentSettings(
      provider: enumFromJson(json['provider'], AiProvider.values, _aiProviderNames, AiProvider.anthropic),
      model: json['model'] as String?,
      baseUrl: json['baseUrl'] as String?,
      isEnabled: json['isEnabled'] as bool? ?? false,
      hasApiKey: json['hasApiKey'] as bool? ?? false,
    );

/// Reads an `AuthenticationSettingsDto`.
AuthenticationSettings authenticationSettingsFromJson(Map<String, dynamic> json) =>
    AuthenticationSettings(
      provider: enumFromJson(
        json['provider'],
        AuthProvider.values,
        _authProviderNames,
        AuthProvider.googleWorkspace,
      ),
      clientId: json['clientId'] as String?,
      authority: json['authority'] as String?,
      tenantId: json['tenantId'] as String?,
      hostedDomain: json['hostedDomain'] as String?,
      isEnabled: json['isEnabled'] as bool? ?? false,
      hasClientSecret: json['hasClientSecret'] as bool? ?? false,
    );

/// Reads a `GitHubSettingsDto`.
GitHubSettings gitHubSettingsFromJson(Map<String, dynamic> json) => GitHubSettings(
      agentPackageRepository: json['agentPackageRepository'] as String? ?? '',
      isAgentPackageRepositoryDefault: json['isAgentPackageRepositoryDefault'] as bool? ?? false,
      scriptApprovalRepository: json['scriptApprovalRepository'] as String? ?? '',
      isScriptApprovalRepositoryDefault: json['isScriptApprovalRepositoryDefault'] as bool? ?? false,
      hasApiToken: json['hasApiToken'] as bool? ?? false,
      hasScriptApprovalToken: json['hasScriptApprovalToken'] as bool? ?? false,
    );

/// Reads a `PatchingPolicySettingsDto`.
PatchingPolicySettings patchingPolicyFromJson(Map<String, dynamic> json) => PatchingPolicySettings(
      intervalValue: (json['intervalValue'] as num?)?.toInt() ?? 7,
      intervalUnit: timeUnitFromJson(json['intervalUnit']),
      delayValue: (json['delayValue'] as num?)?.toInt() ?? 1,
      delayUnit: timeUnitFromJson(json['delayUnit']),
      maxDelayCount: (json['maxDelayCount'] as num?)?.toInt() ?? 3,
    );

PatchingTimeUnit timeUnitFromJson(Object? raw) =>
    enumFromJson(raw, PatchingTimeUnit.values, _timeUnitNames, PatchingTimeUnit.days);

/// Reads a `GooseCliStatus`.
GooseCliStatus gooseCliStatusFromJson(Map<String, dynamic> json) => GooseCliStatus(
      isAvailable: json['isAvailable'] as bool? ?? false,
      version: json['version'] as String?,
      error: json['error'] as String?,
    );
