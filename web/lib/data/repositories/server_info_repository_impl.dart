import '../../core/network/api_client.dart';
import '../../domain/repositories/repositories.dart';

/// Reads `AdminServerController`'s `ServerInfoDto`. One string, so no entity or mapper file —
/// the key is the DTO's `Version` parameter in camelCase, like every other mirrored shape.
class ServerInfoRepositoryImpl implements ServerInfoRepository {
  const ServerInfoRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<String> version() async {
    final json = await _api.getJson('/api/admin/server') as Map<String, dynamic>;
    return json['version'] as String;
  }
}
