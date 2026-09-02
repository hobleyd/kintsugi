import '../../core/network/json_reader.dart';
import '../../domain/entities/agent_package.dart';
import '../../domain/entities/enums.dart';

/// Reads a `ClientsViewDto`.
ClientsView clientsViewFromJson(Map<String, dynamic> json) => ClientsView(
      packages: listFromJson(json['packages'], agentPackageFromJson),
      sourceStatus: json['sourceStatus'] is Map<String, dynamic>
          ? sourceStatusFromJson(json['sourceStatus'] as Map<String, dynamic>)
          : const AgentPackageSourceStatus.unknown(),
      agentApiBaseUrl: json['agentApiBaseUrl'] as String? ?? '',
      agentApiBaseUrlIsDerived: json['agentApiBaseUrlIsDerived'] as bool? ?? false,
      requestBaseUrl: json['requestBaseUrl'] as String? ?? '',
      importResults: listFromJson(json['importResults'], importResultFromJson),
      refreshError: json['refreshError'] as String?,
    );

AgentPackage agentPackageFromJson(Map<String, dynamic> json) => AgentPackage(
      platform: json['platform'] as String? ?? '',
      version: json['version'] as String? ?? '',
      fileName: json['fileName'] as String? ?? '',
      fileSizeBytes: (json['fileSizeBytes'] as num?)?.toInt() ?? 0,
      sha256: json['sha256'] as String? ?? '',
      releaseNotes: json['releaseNotes'] as String?,
      publishedUtc: dateTimeRequiredFromJson(json['publishedUtc']),
    );

AgentPackageSourceStatus sourceStatusFromJson(Map<String, dynamic> json) => AgentPackageSourceStatus(
      sourceDescription: json['sourceDescription'] as String? ?? '',
      platforms: listFromJson(json['platforms'], sourceRowFromJson),
      unavailableReason: json['unavailableReason'] as String?,
    );

AgentPackageSourceRow sourceRowFromJson(Map<String, dynamic> json) => AgentPackageSourceRow(
      platform: json['platform'] as String? ?? '',
      availableVersion: json['availableVersion'] as String? ?? '',
      publishedVersion: json['publishedVersion'] as String?,
      isNewer: json['isNewer'] as bool? ?? false,
    );

AgentPackageImportResult importResultFromJson(Map<String, dynamic> json) => AgentPackageImportResult(
      platform: json['platform'] as String? ?? '',
      version: json['version'] as String? ?? '',
      outcome: enumFromJson(
        json['outcome'],
        AgentPackageImportOutcome.values,
        const ['Imported', 'AlreadyPublished', 'Failed'],
        AgentPackageImportOutcome.failed,
      ),
      message: json['message'] as String?,
    );
