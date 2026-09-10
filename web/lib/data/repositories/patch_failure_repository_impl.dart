import '../../core/network/api_client.dart';
import '../../core/network/json_reader.dart';
import '../../domain/entities/patch_failure.dart';
import '../../domain/repositories/repositories.dart';
import '../models/patch_failure_mapper.dart';

/// Talks to `/api/admin/patch-failures`.
///
/// Under `/api/admin/` for the reason every browser-driven route is: `/api/patch-failures` — the
/// route the agents post to — is inside nginx's exact-match agent regex, so a browser calling it
/// gets a 403 for want of a client certificate.
class PatchFailureRepositoryImpl implements PatchFailureRepository {
  const PatchFailureRepositoryImpl(this._api);

  final ApiClient _api;

  static const _base = '/api/admin/patch-failures';

  @override
  Future<List<PatchFailure>> list() async =>
      listFromJson(await _api.getJson(_base), patchFailureFromJson);

  @override
  Future<void> dismiss(String id) async {
    await _api.postJson('$_base/${Uri.encodeComponent(id)}/dismiss');
  }
}
