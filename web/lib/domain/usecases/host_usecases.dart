import '../entities/host.dart';
import '../repositories/repositories.dart';

class GetHosts {
  const GetHosts(this._repository);

  final HostRepository _repository;

  Future<List<HostSummary>> call() => _repository.list();
}

/// Asks a host's agent to uninstall itself on its next check-in.
class RequestHostRemoval {
  const RequestHostRemoval(this._repository);

  final HostRepository _repository;

  Future<void> call(String id) => _repository.requestRemoval(id);
}
