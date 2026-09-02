import '../../core/network/api_client.dart';
import '../../core/network/json_reader.dart';
import '../../domain/entities/host.dart';
import '../../domain/repositories/repositories.dart';
import '../models/host_mapper.dart';

class HostRepositoryImpl implements HostRepository {
  const HostRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<List<HostSummary>> list() async =>
      listFromJson(await _api.getJson('/api/hosts'), hostFromJson);

  /// Plural `/api/hosts/{id}`, deliberately — the singular `/api/host` is inside nginx's agent
  /// regex and would demand a client certificate this browser has not got.
  @override
  Future<void> requestRemoval(String id) => _api.delete('/api/hosts/${Uri.encodeComponent(id)}');
}
