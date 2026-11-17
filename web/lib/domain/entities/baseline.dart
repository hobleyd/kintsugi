import 'package:equatable/equatable.dart';

import 'enums.dart';

/// How many hosts are in each state for one item or across a baseline. [notReported] is a host the
/// baseline applies to that has sent nothing for it yet.
class ComplianceCounts extends Equatable {
  const ComplianceCounts({
    this.compliant = 0,
    this.drifted = 0,
    this.notApplicable = 0,
    this.error = 0,
    this.notReported = 0,
  });

  final int compliant;
  final int drifted;
  final int notApplicable;
  final int error;
  final int notReported;

  @override
  List<Object?> get props => [compliant, drifted, notApplicable, error, notReported];
}

/// A row of the Baselines screen.
class BaselineSummary extends Equatable {
  const BaselineSummary({
    required this.id,
    required this.name,
    required this.latestVersion,
    required this.signedVersion,
    required this.assignedToAllWindowsHosts,
    required this.applicableHostCount,
    required this.settingCount,
    required this.totals,
  });

  final String id;
  final String name;
  final int latestVersion;

  /// The version agents are given, or null while nothing is signed.
  final int? signedVersion;
  final bool assignedToAllWindowsHosts;
  final int applicableHostCount;
  final int settingCount;
  final ComplianceCounts totals;

  @override
  List<Object?> get props =>
      [id, name, latestVersion, signedVersion, assignedToAllWindowsHosts, applicableHostCount, settingCount, totals];
}

/// One item of a signed baseline document, exactly as the server's `BaselineItem` writes it.
class BaselineItem extends Equatable {
  const BaselineItem({
    required this.key,
    required this.kind,
    required this.scope,
    required this.hive,
    required this.path,
    required this.valueName,
    this.valueType,
    this.data,
    this.secret,
    this.reason,
  });

  final String key;

  /// `setting` or `fact`.
  final String kind;

  /// `machine` or `user`.
  final String scope;
  final String hive;
  final String path;
  final String valueName;
  final String? valueType;
  final String? data;

  /// The name of the secret that supplies the value; the value itself never reaches this client.
  final String? secret;
  final String? reason;

  bool get isSetting => kind == 'setting';

  /// What the setting should read, as the screen shows it.
  String get expected => secret != null ? 'secret: $secret' : '${data ?? ''} (${valueType ?? '?'})';

  /// `HKLM\path\valueName`, the way a reader would type it into regedit.
  String get location => '$hive\\$path${valueName.isEmpty ? '' : '\\$valueName'}';

  @override
  List<Object?> get props => [key, kind, scope, hive, path, valueName, valueType, data, secret, reason];
}

class BaselineVersion extends Equatable {
  const BaselineVersion({
    required this.id,
    required this.number,
    required this.document,
    required this.documentSha256,
    required this.signed,
    this.signedBy,
    this.signedUtc,
    this.createdBy,
    required this.createdUtc,
  });

  final String id;
  final int number;

  /// The canonical JSON the signature covers, byte for byte. Signing hashes *this* string.
  final String document;
  final String documentSha256;
  final bool signed;
  final String? signedBy;
  final DateTime? signedUtc;
  final String? createdBy;
  final DateTime createdUtc;

  @override
  List<Object?> get props => [id, number, document, documentSha256, signed, signedBy, signedUtc, createdBy, createdUtc];
}

class BaselineItemCompliance extends Equatable {
  const BaselineItemCompliance({required this.item, required this.counts});

  final BaselineItem item;
  final ComplianceCounts counts;

  @override
  List<Object?> get props => [item, counts];
}

class BaselineHostCompliance extends Equatable {
  const BaselineHostCompliance({
    required this.hostId,
    required this.hostname,
    required this.counts,
    this.lastCheckedUtc,
  });

  final String hostId;
  final String hostname;
  final ComplianceCounts counts;
  final DateTime? lastCheckedUtc;

  @override
  List<Object?> get props => [hostId, hostname, counts, lastCheckedUtc];
}

class BaselineDetail extends Equatable {
  const BaselineDetail({
    required this.id,
    required this.name,
    required this.assignedToAllWindowsHosts,
    required this.assignedHostNames,
    required this.versions,
    required this.items,
    required this.hosts,
  });

  final String id;
  final String name;
  final bool assignedToAllWindowsHosts;
  final List<String> assignedHostNames;

  /// Newest first.
  final List<BaselineVersion> versions;

  /// The signed version's items, empty until one is signed.
  final List<BaselineItemCompliance> items;
  final List<BaselineHostCompliance> hosts;

  /// The version agents run: the newest signed one.
  BaselineVersion? get signedVersion => versions.where((v) => v.signed).firstOrNull;

  @override
  List<Object?> get props => [id, name, assignedToAllWindowsHosts, assignedHostNames, versions, items, hosts];
}

class HostItemState extends Equatable {
  const HostItemState({
    required this.item,
    this.state,
    this.observedValue,
    this.detail,
    this.lastCheckedUtc,
    this.lastChangedUtc,
  });

  final BaselineItem item;

  /// Null until the host has reported this item.
  final BaselineItemState? state;
  final String? observedValue;
  final String? detail;
  final DateTime? lastCheckedUtc;
  final DateTime? lastChangedUtc;

  @override
  List<Object?> get props => [item, state, observedValue, detail, lastCheckedUtc, lastChangedUtc];
}

class HostFact extends Equatable {
  const HostFact({required this.key, this.value, this.lastCheckedUtc, this.lastChangedUtc});

  final String key;
  final String? value;
  final DateTime? lastCheckedUtc;
  final DateTime? lastChangedUtc;

  @override
  List<Object?> get props => [key, value, lastCheckedUtc, lastChangedUtc];
}

class BaselineHostDetail extends Equatable {
  const BaselineHostDetail({
    required this.hostId,
    required this.hostname,
    this.signedVersion,
    required this.items,
    required this.facts,
  });

  final String hostId;
  final String hostname;
  final int? signedVersion;
  final List<HostItemState> items;
  final List<HostFact> facts;

  @override
  List<Object?> get props => [hostId, hostname, signedVersion, items, facts];
}
