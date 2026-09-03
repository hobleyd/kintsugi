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

/// Mirrors `VantaSettingsDto`.
///
/// The client secret is never carried — [hasClientSecret] reports only whether one is stored, which
/// is what lets the form honestly offer "leave blank to keep the existing one".
class VantaSettings extends Equatable {
  const VantaSettings({
    required this.enabled,
    required this.clientId,
    required this.hasClientSecret,
    required this.apiBaseUrl,
    required this.isApiBaseUrlDefault,
    required this.vulnerableComponentResourceId,
    required this.packageVulnerabilityResourceId,
    required this.consoleBaseUrl,
    required this.severity,
    required this.syncIntervalHours,
    required this.isConfigured,
  });

  final bool enabled;
  final String clientId;
  final bool hasClientSecret;

  /// The effective value, default included — a FedRAMP tenant points this at api.vanta-gov.com.
  final String apiBaseUrl;
  final bool isApiBaseUrlDefault;

  final String vulnerableComponentResourceId;
  final String packageVulnerabilityResourceId;

  /// This server's own browser-facing address, which every synced record links back to.
  final String consoleBaseUrl;

  final double severity;
  final int syncIntervalHours;

  /// Whether a sync could run at all. Reported separately from [enabled] so "switched on but
  /// missing a resource ID" shows on the screen rather than being discovered as a nightly job that
  /// quietly does nothing.
  final bool isConfigured;

  @override
  List<Object?> get props => [
        enabled,
        clientId,
        hasClientSecret,
        apiBaseUrl,
        isApiBaseUrlDefault,
        vulnerableComponentResourceId,
        packageVulnerabilityResourceId,
        consoleBaseUrl,
        severity,
        syncIntervalHours,
        isConfigured,
      ];
}

/// Mirrors `VantaSyncStatusDto` — what the background sync is doing and how the last run went.
class VantaSyncStatus extends Equatable {
  const VantaSyncStatus({
    required this.running,
    required this.startedUtc,
    required this.completedUtc,
    required this.lastRunSucceeded,
    required this.componentCount,
    required this.packageCount,
    required this.message,
  });

  const VantaSyncStatus.unknown()
      : running = false,
        startedUtc = null,
        completedUtc = null,
        lastRunSucceeded = null,
        componentCount = 0,
        packageCount = 0,
        message = null;

  final bool running;
  final DateTime? startedUtc;
  final DateTime? completedUtc;

  /// Null before the first run of this server process — the status is held in memory, so a restart
  /// resets it. Not the same thing as a failure, and the screen says so.
  final bool? lastRunSucceeded;

  final int componentCount;
  final int packageCount;
  final String? message;

  @override
  List<Object?> get props => [
        running,
        startedUtc,
        completedUtc,
        lastRunSucceeded,
        componentCount,
        packageCount,
        message,
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

/// Mirrors `ClaudeAgentSdkStatus`. Same three fields as [GooseCliStatus] and deliberately not the
/// same type: they answer the same question about different things, and `version` here is the
/// version of the `claude` binary installed in the API image.
class ClaudeAgentSdkStatus extends Equatable {
  const ClaudeAgentSdkStatus({
    required this.isAvailable,
    required this.version,
    required this.error,
  });

  final bool isAvailable;
  final String? version;
  final String? error;

  @override
  List<Object?> get props => [isAvailable, version, error];
}
