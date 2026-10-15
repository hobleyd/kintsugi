import '../../core/network/json_reader.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';

const _aiProviderNames = ['Anthropic', 'OpenAI', 'Ollama', 'GooseCli', 'ClaudeAgentSdk', 'Routed'];
const _aiWireProtocolNames = ['OpenAiChatCompletions', 'OpenAiResponses', 'Anthropic', 'Google', 'Ollama'];
const _aiAuthModeNames = ['ApiKey', 'None', 'GoogleCloud'];
const _aiFeatureNames = ['ScriptResearch', 'ScriptRepair', 'CpeSuggestion'];
const _webSearchBackendNames = ['None', 'OllamaWeb', 'Tavily', 'Brave', 'SearXng'];
const _authProviderNames = ['GoogleWorkspace', 'MicrosoftEntra', 'GenericOidc', 'Clerk'];
const _auditProviderNames = [
  'Datadog',
  'GrafanaLoki',
  'GoogleCloudLogging',
  'AwsCloudWatch',
  'AzureMonitor',
  'SplunkHec',
  'GenericHttp',
];
const _timeUnitNames = ['Hours', 'Days'];

/// Reads an `AiAgentSettingsDto`.
AiAgentSettings aiAgentSettingsFromJson(Map<String, dynamic> json) => AiAgentSettings(
      provider: enumFromJson(json['provider'], AiProvider.values, _aiProviderNames, AiProvider.anthropic),
      model: json['model'] as String?,
      baseUrl: json['baseUrl'] as String?,
      isEnabled: json['isEnabled'] as bool? ?? false,
      hasApiKey: json['hasApiKey'] as bool? ?? false,
      webSearchBackend: enumFromJson(
          json['webSearchBackend'], WebSearchBackend.values, _webSearchBackendNames, WebSearchBackend.none),
      hasWebSearchApiKey: json['hasWebSearchApiKey'] as bool? ?? false,
      webSearchBaseUrl: json['webSearchBaseUrl'] as String?,
    );

AiWireProtocol _protocol(Object? raw) =>
    enumFromJson(raw, AiWireProtocol.values, _aiWireProtocolNames, AiWireProtocol.openAiChatCompletions);

/// Reads an `AiConnectionDto`.
AiConnection aiConnectionFromJson(Map<String, dynamic> json) => AiConnection(
      id: json['id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      catalogProviderId: json['catalogProviderId'] as String?,
      protocol: _protocol(json['protocol']),
      baseUrl: json['baseUrl'] as String?,
      authMode: enumFromJson(json['authMode'], AiAuthMode.values, _aiAuthModeNames, AiAuthMode.apiKey),
      hasApiKey: json['hasApiKey'] as bool? ?? false,
      googleCloudProject: json['googleCloudProject'] as String?,
      googleCloudLocation: json['googleCloudLocation'] as String?,
      useHostedWebSearch: json['useHostedWebSearch'] as bool? ?? false,
    );

/// Reads an `AiFeatureRouteDto`.
AiFeatureRoute aiFeatureRouteFromJson(Map<String, dynamic> json) => AiFeatureRoute(
      feature: enumFromJson(json['feature'], AiFeature.values, _aiFeatureNames, AiFeature.scriptResearch),
      connectionId: json['connectionId'] as String? ?? '',
      model: json['model'] as String? ?? '',
    );

/// Reads an `AiRoutingDto`.
AiRouting aiRoutingFromJson(Map<String, dynamic> json) => AiRouting(
      connections: [
        for (final c in json['connections'] as List<dynamic>? ?? const []) aiConnectionFromJson(c as Map<String, dynamic>),
      ],
      routes: [
        for (final r in json['routes'] as List<dynamic>? ?? const []) aiFeatureRouteFromJson(r as Map<String, dynamic>),
      ],
    );

/// Reads an `AiCatalogDto`.
AiCatalog aiCatalogFromJson(Map<String, dynamic> json) => AiCatalog(
      fetchedAt: dateTimeFromJson(json['fetchedAtUtc']),
      isStale: json['isStale'] as bool? ?? false,
      providers: [
        for (final p in json['providers'] as List<dynamic>? ?? const [])
          AiCatalogProvider(
            id: (p as Map<String, dynamic>)['id'] as String? ?? '',
            name: p['name'] as String? ?? '',
            protocol: _protocol(p['protocol']),
            defaultBaseUrl: p['defaultBaseUrl'] as String?,
            requiresApiKey: p['requiresApiKey'] as bool? ?? false,
            usesGoogleCloud: p['usesGoogleCloud'] as bool? ?? false,
            docUrl: p['docUrl'] as String?,
            models: [
              for (final m in p['models'] as List<dynamic>? ?? const [])
                AiCatalogModel(
                  id: (m as Map<String, dynamic>)['id'] as String? ?? '',
                  name: m['name'] as String? ?? '',
                  protocol: _protocol(m['protocol']),
                  toolCall: m['toolCall'] as bool? ?? false,
                  reasoning: m['reasoning'] as bool? ?? false,
                  contextLimit: (m['contextLimit'] as num?)?.toInt(),
                ),
            ],
          ),
      ],
    );

/// Reads an `AiConnectionTestResultDto`.
AiConnectionTestResult aiConnectionTestResultFromJson(Map<String, dynamic> json) => AiConnectionTestResult(
      succeeded: json['succeeded'] as bool? ?? false,
      reply: json['reply'] as String?,
      error: json['error'] as String?,
      elapsedMilliseconds: (json['elapsedMilliseconds'] as num?)?.toInt() ?? 0,
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

/// Reads an `AuditSettingsDto`.
AuditSettings auditSettingsFromJson(Map<String, dynamic> json) => AuditSettings(
      provider: enumFromJson(
        json['provider'],
        AuditProvider.values,
        _auditProviderNames,
        AuditProvider.datadog,
      ),
      isEnabled: json['isEnabled'] as bool? ?? false,
      endpoint: json['endpoint'] as String?,
      region: json['region'] as String?,
      clientId: json['clientId'] as String?,
      hasSecret: json['hasSecret'] as bool? ?? false,
      tenantId: json['tenantId'] as String?,
      projectId: json['projectId'] as String?,
      logGroup: json['logGroup'] as String?,
      dataCollectionRuleId: json['dataCollectionRuleId'] as String?,
      stream: json['stream'] as String?,
      index: json['index'] as String?,
    );

/// Reads a `GitHubSettingsDto`.
GitHubSettings gitHubSettingsFromJson(Map<String, dynamic> json) => GitHubSettings(
      agentPackageRepository: json['agentPackageRepository'] as String? ?? '',
      isAgentPackageRepositoryDefault: json['isAgentPackageRepositoryDefault'] as bool? ?? false,
      scriptApprovalRepository: json['scriptApprovalRepository'] as String? ?? '',
      isScriptApprovalRepositoryDefault: json['isScriptApprovalRepositoryDefault'] as bool? ?? false,
      hasApiToken: json['hasApiToken'] as bool? ?? false,
      hasScriptApprovalToken: json['hasScriptApprovalToken'] as bool? ?? false,
      gitHubAppSlug: json['gitHubAppSlug'] as String?,
      gitHubAppOwner: json['gitHubAppOwner'] as String?,
      isGitHubAppInstalled: json['isGitHubAppInstalled'] as bool? ?? false,
    );

/// Reads a `GitHubAppManifestDto`.
GitHubAppManifest gitHubAppManifestFromJson(Map<String, dynamic> json) => GitHubAppManifest(
      createUrl: json['createUrl'] as String? ?? '',
      manifest: json['manifest'] as String? ?? '',
    );

/// Reads a `VantaSettingsDto`.
VantaSettings vantaSettingsFromJson(Map<String, dynamic> json) => VantaSettings(
      enabled: json['enabled'] as bool? ?? false,
      clientId: json['clientId'] as String? ?? '',
      hasClientSecret: json['hasClientSecret'] as bool? ?? false,
      apiBaseUrl: json['apiBaseUrl'] as String? ?? '',
      isApiBaseUrlDefault: json['isApiBaseUrlDefault'] as bool? ?? false,
      vulnerableComponentResourceId: json['vulnerableComponentResourceId'] as String? ?? '',
      packageVulnerabilityResourceId: json['packageVulnerabilityResourceId'] as String? ?? '',
      consoleBaseUrl: json['consoleBaseUrl'] as String? ?? '',
      severity: (json['severity'] as num?)?.toDouble() ?? 5.0,
      syncIntervalHours: (json['syncIntervalHours'] as num?)?.toInt() ?? 24,
      isConfigured: json['isConfigured'] as bool? ?? false,
    );

/// Reads a `VantaSyncStatusDto`.
VantaSyncStatus vantaSyncStatusFromJson(Map<String, dynamic> json) => VantaSyncStatus(
      running: json['running'] as bool? ?? false,
      startedUtc: dateTimeFromJson(json['startedUtc']),
      completedUtc: dateTimeFromJson(json['completedUtc']),
      lastRunSucceeded: json['lastRunSucceeded'] as bool?,
      componentCount: (json['componentCount'] as num?)?.toInt() ?? 0,
      packageCount: (json['packageCount'] as num?)?.toInt() ?? 0,
      message: json['message'] as String?,
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

/// Reads a `ClaudeAgentSdkStatus`.
ClaudeAgentSdkStatus claudeAgentSdkStatusFromJson(Map<String, dynamic> json) => ClaudeAgentSdkStatus(
      isAvailable: json['isAvailable'] as bool? ?? false,
      version: json['version'] as String?,
      error: json['error'] as String?,
    );
