import '../../core/network/api_client.dart';
import '../../domain/entities/upgrade_path.dart';
import '../../domain/repositories/repositories.dart';
import '../models/upgrade_path_mapper.dart';

/// Talks to the `/api/upgrade-paths/...` sub-routes.
///
/// Sub-routes only, and that is not incidental: the bare `/api/upgrade-paths` is inside nginx's
/// exact-match agent regex — it is the route agents poll for their own upgrade statuses — so a
/// browser calling it gets a 403 for want of a client certificate. Everything below it is outside
/// that regex and carries `[RequireAdminSession]` instead.
class UpgradePathRepositoryImpl implements UpgradePathRepository {
  const UpgradePathRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<RunStarted<UpgradePathScanStatus>> startScan() async {
    final json = await _api.postJson('/api/upgrade-paths/scan') as Map<String, dynamic>;
    return RunStarted(
      started: json['started'] as bool? ?? false,
      status: scanStatusFromJson(json['status'] as Map<String, dynamic>),
    );
  }

  @override
  Future<UpgradePathScanStatus> scanStatus() async =>
      scanStatusFromJson(await _api.getJson('/api/upgrade-paths/scan-status') as Map<String, dynamic>);

  @override
  Future<RunStarted<UpdateCheckStatus>> startUpdateCheck() async {
    final json = await _api.postJson('/api/upgrade-paths/check-updates') as Map<String, dynamic>;
    return RunStarted(
      started: json['started'] as bool? ?? false,
      status: updateCheckStatusFromJson(json['status'] as Map<String, dynamic>),
    );
  }

  @override
  Future<UpdateCheckStatus> updateCheckStatus() async => updateCheckStatusFromJson(
        await _api.getJson('/api/upgrade-paths/check-updates-status') as Map<String, dynamic>,
      );

  @override
  Future<UpdateCheckResult> checkUpdate({
    required String applicationName,
    required String platform,
  }) async =>
      updateCheckResultFromJson(
        await _api.postJson('/api/upgrade-paths/check-update', body: {
          'applicationName': applicationName,
          'platform': platform,
        }) as Map<String, dynamic>,
      );

  @override
  Future<RunStarted<UpgradePathRefreshStatus>> startRefresh({
    required String applicationName,
    String? platform,
    String? instructions,
  }) async {
    final json = await _api.postJson('/api/upgrade-paths/refresh', body: {
      'applicationName': applicationName,
      'platform': platform,
      'instructions': instructions,
    }) as Map<String, dynamic>;

    return RunStarted(
      started: json['started'] as bool? ?? false,
      status: refreshStatusFromJson(json['status'] as Map<String, dynamic>),
    );
  }

  @override
  Future<UpgradePathRefreshStatus> refreshStatus(String applicationName) async => refreshStatusFromJson(
        await _api.getJson(
          '/api/upgrade-paths/refresh-status',
          query: {'applicationName': applicationName},
        ) as Map<String, dynamic>,
      );

  @override
  Future<UpgradePathPrompt> prompt({required String applicationName, String? platform}) async =>
      upgradePathPromptFromJson(
        await _api.getJson(
          '/api/upgrade-paths/prompt',
          query: {'applicationName': applicationName, 'platform': platform},
        ) as Map<String, dynamic>,
      );

  @override
  Future<UpgradePathResult> save(Map<String, dynamic> body) async => upgradePathResultFromJson(
        await _api.postJson('/api/upgrade-paths/save', body: body) as Map<String, dynamic>,
      );

  @override
  Future<UpgradePathResult> signScript({
    required String applicationName,
    required String platform,
  }) async =>
      upgradePathResultFromJson(
        await _api.postJson('/api/upgrade-paths/sign-script', body: {
          'applicationName': applicationName,
          'platform': platform,
        }) as Map<String, dynamic>,
      );
}
