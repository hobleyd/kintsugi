import '../../core/network/json_reader.dart';
import '../../domain/entities/managed_application.dart';

/// Reads a `ManagedApplicationsDto`.
ManagedApplications managedApplicationsFromJson(Map<String, dynamic> json) => ManagedApplications(
      applications: listFromJson(
        json['applications'],
        (entry) => ManagedApplication(
          applicationName: entry['applicationName'] as String? ?? '',
          owner: entry['owner'] as String? ?? '',
          hostCount: (entry['hostCount'] as num?)?.toInt() ?? 0,
        ),
      ),
      owners: stringListFromJson(json['owners']),
    );
