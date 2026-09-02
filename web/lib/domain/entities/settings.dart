import 'package:equatable/equatable.dart';

import 'enums.dart';

/// Mirrors `AiAgentSettingsDto`.
class AiAgentSettings extends Equatable {
  const AiAgentSettings({
    required this.provider,
    required this.model,
    required this.baseUrl,
    required this.isEnabled,
    required this.hasApiKey,
  });

  final AiProvider provider;
  final String? model;
  final String? baseUrl;
  final bool isEnabled;

  /// Whether a key is stored. The key itself never reaches this client, which is what lets the
  /// form honestly offer "leave blank to keep the current one".
  final bool hasApiKey;

  @override
  List<Object?> get props => [provider, model, baseUrl, isEnabled, hasApiKey];
}

/// Mirrors `AuthenticationSettingsDto`.
class AuthenticationSettings extends Equatable {
  const AuthenticationSettings({
    required this.provider,
    required this.clientId,
    required this.authority,
    required this.tenantId,
    required this.hostedDomain,
    required this.isEnabled,
    required this.hasClientSecret,
  });

  final AuthProvider provider;
  final String? clientId;
  final String? authority;
  final String? tenantId;
  final String? hostedDomain;
  final bool isEnabled;
  final bool hasClientSecret;

  @override
  List<Object?> get props =>
      [provider, clientId, authority, tenantId, hostedDomain, isEnabled, hasClientSecret];
}

/// Mirrors `GitHubSettingsDto`.
class GitHubSettings extends Equatable {
  const GitHubSettings({
    required this.agentPackageRepository,
    required this.isAgentPackageRepositoryDefault,
    required this.scriptApprovalRepository,
    required this.isScriptApprovalRepositoryDefault,
    required this.hasApiToken,
    required this.hasScriptApprovalToken,
  });

  /// The effective value, defaults included, rather than a blank — the operator should see which
  /// repository this server is actually pointed at.
  final String agentPackageRepository;

  final bool isAgentPackageRepositoryDefault;
  final String scriptApprovalRepository;
  final bool isScriptApprovalRepositoryDefault;
  final bool hasApiToken;
  final bool hasScriptApprovalToken;

  @override
  List<Object?> get props => [
        agentPackageRepository,
        isAgentPackageRepositoryDefault,
        scriptApprovalRepository,
        isScriptApprovalRepositoryDefault,
        hasApiToken,
        hasScriptApprovalToken,
      ];
}

/// Mirrors `PatchingPolicySettingsDto`.
class PatchingPolicySettings extends Equatable {
  const PatchingPolicySettings({
    required this.intervalValue,
    required this.intervalUnit,
    required this.delayValue,
    required this.delayUnit,
    required this.maxDelayCount,
  });

  final int intervalValue;
  final PatchingTimeUnit intervalUnit;
  final int delayValue;
  final PatchingTimeUnit delayUnit;

  /// How many times a required restart or reboot can be postponed before the agent must force it
  /// through. Zero means it can never be deferred.
  final int maxDelayCount;

  @override
  List<Object?> get props => [intervalValue, intervalUnit, delayValue, delayUnit, maxDelayCount];
}

/// Mirrors `GooseCliStatus`.
class GooseCliStatus extends Equatable {
  const GooseCliStatus({required this.isAvailable, required this.version, required this.error});

  final bool isAvailable;
  final String? version;
  final String? error;

  @override
  List<Object?> get props => [isAvailable, version, error];
}
