import '../../core/network/json_reader.dart';
import '../../domain/entities/application.dart';
import 'upgrade_path_mapper.dart';

/// Reads an `ApplicationOverviewDto`.
ApplicationOverview applicationOverviewFromJson(Map<String, dynamic> json) => ApplicationOverview(
      applications: listFromJson(json['applications'], applicationRowFromJson),
      totalApplicationCount: (json['totalApplicationCount'] as num?)?.toInt() ?? 0,
      allHostNames: stringListFromJson(json['allHostNames']),
    );

/// Reads an `ApplicationRowDto`. Recursive, because a package manager's applications are nested
/// under it.
ApplicationRow applicationRowFromJson(Map<String, dynamic> json) => ApplicationRow(
      name: json['name'] as String? ?? '',
      hostCount: (json['hostCount'] as num?)?.toInt() ?? 0,
      hostNames: stringListFromJson(json['hostNames']),
      upgradePaths: listFromJson(json['upgradePaths'], upgradePathSummaryFromJson),
      children: listFromJson(json['children'], applicationRowFromJson),
    );
