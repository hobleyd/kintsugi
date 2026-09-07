import '../entities/agent_package.dart';
import '../repositories/repositories.dart';

class GetClientsView {
  const GetClientsView(this._repository);

  final AgentPackageRepository _repository;

  Future<ClientsView> call() => _repository.view();
}

/// Publishes whatever the upstream repository has that this server does not.
class RefreshClients {
  const RefreshClients(this._repository);

  final AgentPackageRepository _repository;

  Future<ClientsView> call() => _repository.refresh();
}

/// The Windows deployment script for CrowdStrike, rendered by the server with this build's
/// checksum, this server's address and the current enrollment token already in it.
class GetWindowsBootstrapScript {
  const GetWindowsBootstrapScript(this._repository);

  final AgentPackageRepository _repository;

  Future<WindowsBootstrapScript> call() => _repository.windowsBootstrapScript();
}
