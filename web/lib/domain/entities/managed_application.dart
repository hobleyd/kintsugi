import 'package:equatable/equatable.dart';

/// One application another tool patches. Mirrors `ManagedApplicationDto`.
class ManagedApplication extends Equatable {
  const ManagedApplication({required this.applicationName, required this.owner, required this.hostCount});

  final String applicationName;

  /// Who patches it — a name from [ManagedApplications.owners].
  final String owner;
  final int hostCount;

  @override
  List<Object?> get props => [applicationName, owner, hostCount];
}

/// Everything the Managed Elsewhere page shows. Mirrors `ManagedApplicationsDto`.
class ManagedApplications extends Equatable {
  const ManagedApplications({required this.applications, required this.owners});

  /// By owner, then by name, as the server sends them.
  final List<ManagedApplication> applications;

  /// The tools that can own an application, which the owner pickers offer. An administrator edits it.
  final List<String> owners;

  bool ownerIsInUse(String owner) =>
      applications.any((a) => a.owner.toLowerCase() == owner.toLowerCase());

  @override
  List<Object?> get props => [applications, owners];
}
