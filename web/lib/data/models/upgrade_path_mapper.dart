import '../../core/network/json_reader.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/upgrade_path.dart';

const _upgradePathStatusNames = ['Found', 'NotFound', 'Failed'];
const _upgradeMethodNames = [
  'Unknown',
  'DirectDownload',
  'PackageManagerCommand',
  'ManualSteps',
  'Script',
];
const _approvalOutcomeNames = [
  'Disabled',
  'AlreadyApproved',
  'PullRequestAlreadyOpen',
  'PullRequestOpened',
  'Failed',
  'Unknown',
];

UpgradePathStatus upgradePathStatusFromJson(Object? raw) => enumFromJson(
      raw,
      UpgradePathStatus.values,
      _upgradePathStatusNames,
      UpgradePathStatus.failed,
    );

UpgradeMethod upgradeMethodFromJson(Object? raw) =>
    enumFromJson(raw, UpgradeMethod.values, _upgradeMethodNames, UpgradeMethod.unknown);

/// The wire name for an [UpgradeMethod], for the save route's body.
///
/// `UpgradeMethod` is read server-side by `LenientEnumConverter`, which accepts a name in any
/// casing but *only* a name — never an ordinal — so this has to be a string.
String upgradeMethodToJson(UpgradeMethod method) => _upgradeMethodNames[method.index];

/// Reads a `UpgradePathSummaryDto`.
UpgradePathSummary upgradePathSummaryFromJson(Map<String, dynamic> json) => UpgradePathSummary(
      applicationName: json['applicationName'] as String? ?? '',
      platform: json['platform'] as String? ?? '',
      status: upgradePathStatusFromJson(json['status']),
      // Computed server-side and sent as `statusKey`. The fallback matters only for a response
      // predating that field; 'not-checked' is the same thing the table shows for a row with no
      // researched path at all, which is the safest thing to be wrong about.
      statusKey: json['statusKey'] as String? ?? 'not-checked',
      latestVersion: json['latestVersion'] as String?,
      method: upgradeMethodFromJson(json['method']),
      downloadUrl: json['downloadUrl'] as String?,
      command: json['command'] as String?,
      instructions: json['instructions'] as String?,
      sourceUrl: json['sourceUrl'] as String?,
      notes: json['notes'] as String?,
      checkedUtc: dateTimeRequiredFromJson(json['checkedUtc']),
      hostCount: (json['hostCount'] as num?)?.toInt() ?? 0,
      upToDateHostCount: (json['upToDateHostCount'] as num?)?.toInt() ?? 0,
      updateAvailableHostCount: (json['updateAvailableHostCount'] as num?)?.toInt() ?? 0,
      hostNames: stringListFromJson(json['hostNames']),
      hostNamesNeedingUpdate: stringListFromJson(json['hostNamesNeedingUpdate']),
      script: json['script'] as String?,
      scriptSignature: json['scriptSignature'] as String?,
    );

/// Reads a `UpgradePathResultDto` — or a `RefreshedUpgradePathDto`, which is the same shape minus
/// the signing and approval fields. Those default rather than being required for that reason: a
/// freshly researched result carries no signature yet, which is exactly why it is signable.
UpgradePathResult upgradePathResultFromJson(Map<String, dynamic> json) => UpgradePathResult(
      applicationName: json['applicationName'] as String? ?? '',
      platform: json['platform'] as String? ?? '',
      status: upgradePathStatusFromJson(json['status']),
      latestVersion: json['latestVersion'] as String?,
      method: upgradeMethodFromJson(json['method']),
      downloadUrl: json['downloadUrl'] as String?,
      command: json['command'] as String?,
      instructions: json['instructions'] as String?,
      sourceUrl: json['sourceUrl'] as String?,
      notes: json['notes'] as String?,
      checkedUtc: dateTimeRequiredFromJson(json['checkedUtc']),
      script: json['script'] as String?,
      scriptSigned: json['scriptSigned'] as bool? ?? false,
      approvalOutcome: json.containsKey('approvalOutcome') && json['approvalOutcome'] != null
          ? enumFromJson(
              json['approvalOutcome'],
              ScriptApprovalPublishOutcome.values,
              _approvalOutcomeNames,
              ScriptApprovalPublishOutcome.unknown,
            )
          : null,
      approvalPullRequestUrl: json['approvalPullRequestUrl'] as String?,
      approvalMessage: json['approvalMessage'] as String?,
      raw: json,
    );

/// Reads a `UpgradePathPromptDto`.
UpgradePathPrompt upgradePathPromptFromJson(Map<String, dynamic> json) => UpgradePathPrompt(
      available: json['available'] as bool? ?? false,
      platform: json['platform'] as String?,
      prompt: json['prompt'] as String?,
      reason: json['reason'] as String?,
      existingResult: json['existingResult'] is Map<String, dynamic>
          ? upgradePathResultFromJson(json['existingResult'] as Map<String, dynamic>)
          : null,
    );

/// Reads a `UpgradePathScanStatusDto`.
UpgradePathScanStatus scanStatusFromJson(Map<String, dynamic> json) => UpgradePathScanStatus(
      isRunning: json['isRunning'] as bool? ?? false,
      total: (json['total'] as num?)?.toInt() ?? 0,
      completed: (json['completed'] as num?)?.toInt() ?? 0,
      resolved: (json['resolved'] as num?)?.toInt() ?? 0,
      notFound: (json['notFound'] as num?)?.toInt() ?? 0,
      failed: (json['failed'] as num?)?.toInt() ?? 0,
      skipped: (json['skipped'] as num?)?.toInt() ?? 0,
      startedUtc: dateTimeFromJson(json['startedUtc']),
      completedUtc: dateTimeFromJson(json['completedUtc']),
      faultReason: json['faultReason'] as String?,
      notes: stringListFromJson(json['notes']),
    );

/// Reads a `UpdateCheckStatusDto`.
UpdateCheckStatus updateCheckStatusFromJson(Map<String, dynamic> json) => UpdateCheckStatus(
      isRunning: json['isRunning'] as bool? ?? false,
      total: (json['total'] as num?)?.toInt() ?? 0,
      completed: (json['completed'] as num?)?.toInt() ?? 0,
      updated: (json['updated'] as num?)?.toInt() ?? 0,
      unchanged: (json['unchanged'] as num?)?.toInt() ?? 0,
      failed: (json['failed'] as num?)?.toInt() ?? 0,
      startedUtc: dateTimeFromJson(json['startedUtc']),
      completedUtc: dateTimeFromJson(json['completedUtc']),
      faultReason: json['faultReason'] as String?,
    );

/// Reads a `UpgradePathRefreshStatusDto`, flattening its nested `RefreshUpgradePathResult`.
///
/// The nesting is not reproduced in the entity because nothing on screen needs the distinction:
/// "did the run succeed" and "what did it produce" are both properties of the same refresh, and a
/// null result and a failed one both show as failed.
UpgradePathRefreshStatus refreshStatusFromJson(Map<String, dynamic> json) {
  final result = json['result'];
  final resultMap = result is Map<String, dynamic> ? result : null;

  return UpgradePathRefreshStatus(
    isRunning: json['isRunning'] as bool? ?? false,
    startedUtc: dateTimeFromJson(json['startedUtc']),
    completedUtc: dateTimeFromJson(json['completedUtc']),
    success: resultMap?['success'] as bool?,
    errorMessage: resultMap?['errorMessage'] as String?,
    results: listFromJson(resultMap?['results'], upgradePathResultFromJson),
  );
}
