import '../../core/network/api_client.dart';
import '../../domain/entities/host_activity.dart';
import '../../domain/repositories/repositories.dart';
import '../models/host_activity_mapper.dart';

/// Talks to `/api/admin/host-activity`.
class HostActivityRepositoryImpl implements HostActivityRepository {
  const HostActivityRepositoryImpl(this._api);

  final ApiClient _api;

  static const _base = '/api/admin/host-activity';

  @override
  Future<HostActivity> get(String hostId) async {
    final json = await _api.getJson('$_base/${Uri.encodeComponent(hostId)}');
    return hostActivityFromJson(json as Map<String, dynamic>);
  }

  @override
  Future<int> setRetention(int months) async {
    final json = await _api.putJson('$_base/retention', body: {'retentionMonths': months});
    return ((json as Map<String, dynamic>)['retentionMonths'] as num?)?.toInt() ?? months;
  }
}
