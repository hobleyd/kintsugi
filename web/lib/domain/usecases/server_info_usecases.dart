import '../repositories/repositories.dart';

/// Reads the server's own version, shown under the Kintsugi name in the sidebar.
class GetServerVersion {
  const GetServerVersion(this._repository);

  final ServerInfoRepository _repository;

  Future<String> call() => _repository.version();
}
