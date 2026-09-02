import '../../core/network/json_reader.dart';
import '../../domain/entities/upgrade_script.dart';

/// Reads an `UpgradeScriptsViewDto`.
UpgradeScriptsView upgradeScriptsViewFromJson(Map<String, dynamic> json) => UpgradeScriptsView(
      overview: overviewFromJson(json['overview'] as Map<String, dynamic>),
      importResult: json['importResult'] is Map<String, dynamic>
          ? importApprovedScriptsResultFromJson(json['importResult'] as Map<String, dynamic>)
          : null,
      refreshError: json['refreshError'] as String?,
      adopted: json['adopted'] is Map<String, dynamic>
          ? adoptedFromJson(json['adopted'] as Map<String, dynamic>)
          : null,
      adoptError: json['adoptError'] as String?,
      tookServerScript: json['tookServerScript'] is Map<String, dynamic>
          ? tookServerScriptFromJson(json['tookServerScript'] as Map<String, dynamic>)
          : null,
      takeServerScriptError: json['takeServerScriptError'] as String?,
    );

UpgradeScriptsOverview overviewFromJson(Map<String, dynamic> json) => UpgradeScriptsOverview(
      repository: json['repository'] as String? ?? '',
      defaultBranch: json['defaultBranch'] as String?,
      headCommitSha: json['headCommitSha'] as String?,
      unavailableReason: json['unavailableReason'] as String?,
      publishingEnabled: json['publishingEnabled'] as bool? ?? false,
      thisServerFingerprint: json['thisServerFingerprint'] as String? ?? '',
      approved: listFromJson(json['approved'], approvedScriptFromJson),
      localScripts: listFromJson(json['localScripts'], localScriptFromJson),
      adoptionCandidates: listFromJson(json['adoptionCandidates'], adoptionCandidateFromJson),
    );

ApprovedScript approvedScriptFromJson(Map<String, dynamic> json) => ApprovedScript(
      sha256: json['sha256'] as String? ?? '',
      platformBucket: json['platformBucket'] as String? ?? '',
      applicationName: json['applicationName'] as String? ?? '',
      signerFingerprint: json['signerFingerprint'] as String? ?? '',
      isThisServer: json['isThisServer'] as bool? ?? false,
      signedBy: json['signedBy'] as String?,
      approvedAtUtc: dateTimeRequiredFromJson(json['approvedAtUtc']),
      sourceCommitSha: json['sourceCommitSha'] as String? ?? '',
      heldLocally: json['heldLocally'] as bool? ?? false,
    );

LocalScript localScriptFromJson(Map<String, dynamic> json) => LocalScript(
      applicationName: json['applicationName'] as String? ?? '',
      platform: json['platform'] as String? ?? '',
      sha256: json['sha256'] as String? ?? '',
      signed: json['signed'] as bool? ?? false,
      approvedUpstream: json['approvedUpstream'] as bool? ?? false,
      newerServerScriptAvailable: json['newerServerScriptAvailable'] as bool? ?? false,
    );

AdoptionCandidate adoptionCandidateFromJson(Map<String, dynamic> json) => AdoptionCandidate(
      applicationName: json['applicationName'] as String? ?? '',
      platform: json['platform'] as String? ?? '',
      sha256: json['sha256'] as String? ?? '',
      signerFingerprint: json['signerFingerprint'] as String? ?? '',
      isThisServer: json['isThisServer'] as bool? ?? false,
      signedBy: json['signedBy'] as String?,
      approvedAtUtc: dateTimeRequiredFromJson(json['approvedAtUtc']),
      replacesExistingScript: json['replacesExistingScript'] as bool? ?? false,
    );

ImportApprovedScriptsResult importApprovedScriptsResultFromJson(Map<String, dynamic> json) =>
    ImportApprovedScriptsResult(
      repository: json['repository'] as String? ?? '',
      commitSha: json['commitSha'] as String?,
      imported: (json['imported'] as num?)?.toInt() ?? 0,
      alreadyKnown: (json['alreadyKnown'] as num?)?.toInt() ?? 0,
      blessed: listFromJson(json['blessed'], blessedFromJson),
      rejected: stringListFromJson(json['rejected']),
    );

BlessedUpgradePath blessedFromJson(Map<String, dynamic> json) => BlessedUpgradePath(
      applicationName: json['applicationName'] as String? ?? '',
      platform: json['platform'] as String? ?? '',
      signerFingerprint: json['signerFingerprint'] as String? ?? '',
    );

AdoptedScript adoptedFromJson(Map<String, dynamic> json) => AdoptedScript(
      applicationName: json['applicationName'] as String? ?? '',
      platform: json['platform'] as String? ?? '',
      sha256: json['sha256'] as String? ?? '',
      signerFingerprint: json['signerFingerprint'] as String? ?? '',
    );

TookServerScript tookServerScriptFromJson(Map<String, dynamic> json) => TookServerScript(
      applicationName: json['applicationName'] as String? ?? '',
      platform: json['platform'] as String? ?? '',
      changed: json['changed'] as bool? ?? false,
    );
