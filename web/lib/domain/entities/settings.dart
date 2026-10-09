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
    this.webSearchBackend = WebSearchBackend.none,
    this.hasWebSearchApiKey = false,
    this.webSearchBaseUrl,
  });

  final AiProvider provider;
  final String? model;
  final String? baseUrl;
  final bool isEnabled;

  /// Whether a key is stored. The key itself never reaches this client, which is what lets the
  /// form honestly offer "leave blank to keep the current one".
  final bool hasApiKey;

  /// The search service behind Kintsugi's own `web_search` tool.
  final WebSearchBackend webSearchBackend;
  final bool hasWebSearchApiKey;
  final String? webSearchBaseUrl;

  @override
  List<Object?> get props =>
      [provider, model, baseUrl, isEnabled, hasApiKey, webSearchBackend, hasWebSearchApiKey, webSearchBaseUrl];
}

/// Mirrors `AiConnectionDto`.
class AiConnection extends Equatable {
  const AiConnection({
    required this.id,
    required this.name,
    required this.catalogProviderId,
    required this.protocol,
    required this.baseUrl,
    required this.authMode,
    required this.hasApiKey,
    required this.googleCloudProject,
    required this.googleCloudLocation,
    required this.useHostedWebSearch,
  });

  final String id;
  final String name;
  final String? catalogProviderId;
  final AiWireProtocol protocol;
  final String? baseUrl;
  final AiAuthMode authMode;
  final bool hasApiKey;
  final String? googleCloudProject;
  final String? googleCloudLocation;
  final bool useHostedWebSearch;

  /// Where requests go, as a person would describe it.
  String get endpointLabel => authMode == AiAuthMode.googleCloud && googleCloudProject != null
      ? 'Vertex AI · $googleCloudProject · $googleCloudLocation'
      : baseUrl ?? 'protocol default';

  @override
  List<Object?> get props => [
        id,
        name,
        catalogProviderId,
        protocol,
        baseUrl,
        authMode,
        hasApiKey,
        googleCloudProject,
        googleCloudLocation,
        useHostedWebSearch,
      ];
}

/// What the connection editor sends. A null [id] creates; a null [apiKey] keeps the stored key.
class AiConnectionDraft {
  const AiConnectionDraft({
    required this.id,
    required this.name,
    required this.catalogProviderId,
    required this.protocol,
    required this.baseUrl,
    required this.authMode,
    required this.apiKey,
    required this.googleCloudProject,
    required this.googleCloudLocation,
    required this.useHostedWebSearch,
  });

  final String? id;
  final String name;
  final String? catalogProviderId;
  final AiWireProtocol protocol;
  final String? baseUrl;
  final AiAuthMode authMode;
  final String? apiKey;
  final String? googleCloudProject;
  final String? googleCloudLocation;
  final bool useHostedWebSearch;
}

/// Mirrors `AiFeatureRouteDto`.
class AiFeatureRoute extends Equatable {
  const AiFeatureRoute({required this.feature, required this.connectionId, required this.model});

  final AiFeature feature;
  final String connectionId;
  final String model;

  @override
  List<Object?> get props => [feature, connectionId, model];
}

/// Mirrors `AiRoutingDto`.
class AiRouting extends Equatable {
  const AiRouting({required this.connections, required this.routes});

  final List<AiConnection> connections;
  final List<AiFeatureRoute> routes;

  AiFeatureRoute? routeFor(AiFeature feature) => routes.where((r) => r.feature == feature).firstOrNull;

  AiConnection? connection(String id) => connections.where((c) => c.id == id).firstOrNull;

  /// The server's fallback chain (`AiProviderSettings.RouteFor`): which route a feature actually
  /// runs on when it has none of its own.
  AiFeatureRoute? effectiveRouteFor(AiFeature feature) {
    final chain = switch (feature) {
      AiFeature.cpeSuggestion => [AiFeature.cpeSuggestion, AiFeature.scriptRepair, AiFeature.scriptResearch],
      AiFeature.scriptRepair => [AiFeature.scriptRepair, AiFeature.scriptResearch],
      AiFeature.scriptResearch => [AiFeature.scriptResearch],
    };
    for (final candidate in chain) {
      final route = routeFor(candidate);
      if (route != null) return route;
    }
    return null;
  }

  @override
  List<Object?> get props => [connections, routes];
}

/// Mirrors `AiCatalogModelDto`.
class AiCatalogModel extends Equatable {
  const AiCatalogModel({
    required this.id,
    required this.name,
    required this.protocol,
    required this.toolCall,
    required this.reasoning,
    required this.contextLimit,
  });

  final String id;
  final String name;
  final AiWireProtocol protocol;
  final bool toolCall;
  final bool reasoning;
  final int? contextLimit;

  @override
  List<Object?> get props => [id, name, protocol, toolCall, reasoning, contextLimit];
}

/// Mirrors `AiCatalogProviderDto`.
class AiCatalogProvider extends Equatable {
  const AiCatalogProvider({
    required this.id,
    required this.name,
    required this.protocol,
    required this.defaultBaseUrl,
    required this.requiresApiKey,
    required this.usesGoogleCloud,
    required this.docUrl,
    required this.models,
  });

  final String id;
  final String name;
  final AiWireProtocol protocol;
  final String? defaultBaseUrl;
  final bool requiresApiKey;
  final bool usesGoogleCloud;
  final String? docUrl;
  final List<AiCatalogModel> models;

  @override
  List<Object?> get props => [id, name, protocol, defaultBaseUrl, requiresApiKey, usesGoogleCloud, docUrl, models];
}

/// Mirrors `AiCatalogDto`.
class AiCatalog extends Equatable {
  const AiCatalog({required this.fetchedAt, required this.isStale, required this.providers});

  static const empty = AiCatalog(fetchedAt: null, isStale: false, providers: []);

  final DateTime? fetchedAt;
  final bool isStale;
  final List<AiCatalogProvider> providers;

  AiCatalogProvider? provider(String? id) => id == null ? null : providers.where((p) => p.id == id).firstOrNull;

  @override
  List<Object?> get props => [fetchedAt, isStale, providers];
}

/// Mirrors `AiConnectionTestResultDto`.
class AiConnectionTestResult extends Equatable {
  const AiConnectionTestResult({
    required this.succeeded,
    required this.reply,
    required this.error,
    required this.elapsedMilliseconds,
  });

  final bool succeeded;
  final String? reply;
  final String? error;
  final int elapsedMilliseconds;

  @override
  List<Object?> get props => [succeeded, reply, error, elapsedMilliseconds];
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

/// Mirrors `AuditSettingsDto`.
///
/// Which field a provider reads is documented on the C# entity `AuditSettings`; the ones the
/// selected provider does not use arrive null. The secret is never carried — [hasSecret] reports
/// only whether one is stored.
class AuditSettings extends Equatable {
  const AuditSettings({
    required this.provider,
    required this.isEnabled,
    required this.endpoint,
    required this.region,
    required this.clientId,
    required this.hasSecret,
    required this.tenantId,
    required this.projectId,
    required this.logGroup,
    required this.dataCollectionRuleId,
    required this.stream,
    required this.index,
  });

  final AuditProvider provider;
  final bool isEnabled;
  final String? endpoint;
  final String? region;
  final String? clientId;
  final bool hasSecret;
  final String? tenantId;
  final String? projectId;
  final String? logGroup;
  final String? dataCollectionRuleId;
  final String? stream;
  final String? index;

  @override
  List<Object?> get props => [
        provider,
        isEnabled,
        endpoint,
        region,
        clientId,
        hasSecret,
        tenantId,
        projectId,
        logGroup,
        dataCollectionRuleId,
        stream,
        index,
      ];
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
    this.gitHubAppSlug,
    this.gitHubAppOwner,
    this.isGitHubAppInstalled = false,
  });

  /// The effective value, defaults included, rather than a blank — the operator should see which
  /// repository this server is actually pointed at.
  final String agentPackageRepository;

  final bool isAgentPackageRepositoryDefault;
  final String scriptApprovalRepository;
  final bool isScriptApprovalRepositoryDefault;
  final bool hasApiToken;
  final bool hasScriptApprovalToken;

  /// The connected GitHub App's slug, or null when none is connected.
  final String? gitHubAppSlug;

  /// The account that owns the connected App — which is the only account its installation tokens
  /// can reach, so the screen sets it against the approval repository's owner.
  final String? gitHubAppOwner;

  /// Whether the App is installed, which is when it takes over from both tokens.
  final bool isGitHubAppInstalled;

  bool get hasGitHubApp => gitHubAppSlug != null;

  /// GitHub's own page for the App's installation — where an administrator installs it, or
  /// changes which repositories it can reach.
  String? get gitHubAppInstallUrl => gitHubAppSlug == null
      ? null
      : 'https://github.com/apps/${Uri.encodeComponent(gitHubAppSlug!)}/installations/new';

  @override
  List<Object?> get props => [
        agentPackageRepository,
        isAgentPackageRepositoryDefault,
        scriptApprovalRepository,
        isScriptApprovalRepositoryDefault,
        hasApiToken,
        hasScriptApprovalToken,
        gitHubAppSlug,
        gitHubAppOwner,
        isGitHubAppInstalled,
      ];
}

/// Mirrors `GitHubAppManifestDto`: GitHub's create-App page and the manifest to post to it.
class GitHubAppManifest extends Equatable {
  const GitHubAppManifest({required this.createUrl, required this.manifest});

  final String createUrl;

  /// The manifest JSON, posted as the form's `manifest` field.
  final String manifest;

  @override
  List<Object?> get props => [createUrl, manifest];
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
