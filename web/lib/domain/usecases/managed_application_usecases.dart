import '../entities/managed_application.dart';
import '../repositories/repositories.dart';

/// The applications another tool patches and the tools that can own one.
class GetManagedApplications {
  const GetManagedApplications(this._repository);

  final ManagedApplicationRepository _repository;

  Future<ManagedApplications> call() => _repository.read();
}

/// Says that [owner] patches each of [names]. Marking one stops Kintsugi researching and patching it,
/// on every host.
class MarkApplicationsManaged {
  const MarkApplicationsManaged(this._repository);

  final ManagedApplicationRepository _repository;

  Future<ManagedApplications> call({required List<String> names, required String owner}) =>
      _repository.mark(names: names, owner: owner);
}

/// Gives an application back to Kintsugi.
class UnmarkApplicationManaged {
  const UnmarkApplicationManaged(this._repository);

  final ManagedApplicationRepository _repository;

  Future<ManagedApplications> call(String applicationName) => _repository.unmark(applicationName);
}

class AddManagedOwner {
  const AddManagedOwner(this._repository);

  final ManagedApplicationRepository _repository;

  Future<ManagedApplications> call(String name) => _repository.addOwner(name);
}

/// Takes a tool off the list. The server refuses while an application is still managed by it.
class RemoveManagedOwner {
  const RemoveManagedOwner(this._repository);

  final ManagedApplicationRepository _repository;

  Future<ManagedApplications> call(String name) => _repository.removeOwner(name);
}
