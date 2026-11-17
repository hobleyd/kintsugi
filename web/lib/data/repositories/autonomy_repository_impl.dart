import '../../core/network/api_client.dart';
import '../../domain/entities/autonomy.dart';
import '../../domain/repositories/repositories.dart';
import '../models/autonomy_mapper.dart';

/// Talks to `/api/admin/autonomy`.
class AutonomyRepositoryImpl implements AutonomyRepository {
  const AutonomyRepositoryImpl(this._api);

  final ApiClient _api;

  static const _base = '/api/admin/autonomy';

  @override
  Future<Autonomy> get(AutonomyPeriod period, {String? month}) async {
    final json = await _api.getJson(
      _base,
      query: {'period': period.wire, if (period == AutonomyPeriod.month && month != null) 'month': month},
    );
    return autonomyFromJson(json as Map<String, dynamic>);
  }

  @override
  Future<AutonomyDevicesPage> devices(String key, {int skip = 0, int take = 25, String? search}) async {
    final json = await _api.getJson(
      '$_base/causes/${Uri.encodeComponent(key)}/devices',
      query: {'skip': '$skip', 'take': '$take', if (search != null && search.isNotEmpty) 'search': search},
    );
    return autonomyDevicesPageFromJson(json as Map<String, dynamic>);
  }

  @override
  Future<AutonomyPatchNowResult> patchNow(String key, {required int expectedConnected, required bool includeOffline}) async {
    final json = await _api.postJson(
      '$_base/causes/${Uri.encodeComponent(key)}/patch-now',
      body: {'expectedConnected': expectedConnected, 'includeOffline': includeOffline},
    );
    return autonomyPatchNowResultFromJson(json as Map<String, dynamic>);
  }
}
