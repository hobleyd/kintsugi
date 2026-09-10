import '../../core/network/json_reader.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/patch_failure.dart';
import 'upgrade_path_mapper.dart';

const _resolutionNames = ['Outstanding', 'PatchSucceeded', 'Dismissed', 'ScriptRepaired'];

PatchFailureResolution patchFailureResolutionFromJson(Object? raw) => enumFromJson(
      raw,
      PatchFailureResolution.values,
      _resolutionNames,
      PatchFailureResolution.outstanding,
    );

/// Reads a `PatchFailureDto`. Hand-mirrored — see `.claude/rules/hand-mirrored-dtos.md`; nothing
/// in CI cross-checks this against the C# record.
PatchFailure patchFailureFromJson(Map<String, dynamic> json) => PatchFailure(
      id: json['id'] as String? ?? '',
      hostId: json['hostId'] as String? ?? '',
      hostname: json['hostname'] as String? ?? '',
      serialNumber: json['serialNumber'] as String?,
      applicationName: json['applicationName'] as String? ?? '',
      platform: json['platform'] as String?,
      installedVersion: json['installedVersion'] as String?,
      attemptedVersion: json['attemptedVersion'] as String?,
      details: json['details'] as String? ?? '',
      firstFailedUtc: dateTimeRequiredFromJson(json['firstFailedUtc']),
      lastFailedUtc: dateTimeRequiredFromJson(json['lastFailedUtc']),
      failureCount: (json['failureCount'] as num?)?.toInt() ?? 1,
      resolution: patchFailureResolutionFromJson(json['resolution']),
      resolvedUtc: dateTimeFromJson(json['resolvedUtc']),
      hasScript: json['hasScript'] as bool? ?? false,
      scriptSigned: json['scriptSigned'] as bool? ?? false,
      method: upgradeMethodFromJson(json['method']),
      // Computed server-side. Falling back to the same join this client *can* see keeps an older
      // server's response usable rather than greying out every fix button.
      canFix: json['canFix'] as bool? ??
          (json['platform'] != null && (json['hasScript'] as bool? ?? false)),
    );
