import '../../core/network/api_client.dart';
import '../../domain/entities/upgrade_script.dart';
import '../../domain/repositories/repositories.dart';
import '../models/upgrade_script_mapper.dart';

class UpgradeScriptRepositoryImpl implements UpgradeScriptRepository {
  const UpgradeScriptRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<UpgradeScriptsView> view() async => upgradeScriptsViewFromJson(
        await _api.getJson('/api/admin/upgrade-scripts') as Map<String, dynamic>,
      );

  @override
  Future<UpgradeScriptsView> refresh() async => upgradeScriptsViewFromJson(
        await _api.postJson('/api/admin/upgrade-scripts/refresh') as Map<String, dynamic>,
      );

  @override
  Future<UpgradeScriptsView> adopt({
    required String applicationName,
    required String platform,
    required String sha256,
    required String signerFingerprint,
  }) async =>
      upgradeScriptsViewFromJson(
        await _api.postJson('/api/admin/upgrade-scripts/adopt', body: {
          'applicationName': applicationName,
          'platform': platform,
          'sha256': sha256,
          'signerFingerprint': signerFingerprint,
        }) as Map<String, dynamic>,
      );

  @override
  Future<UpgradeScriptsView> takeServerScript({
    required String applicationName,
    required String platform,
  }) async =>
      upgradeScriptsViewFromJson(
        await _api.postJson('/api/admin/upgrade-scripts/take-server-script', body: {
          'applicationName': applicationName,
          'platform': platform,
        }) as Map<String, dynamic>,
      );
}
