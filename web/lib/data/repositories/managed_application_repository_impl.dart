import '../../core/network/api_client.dart';
import '../../domain/entities/managed_application.dart';
import '../../domain/repositories/repositories.dart';
import '../models/managed_application_mapper.dart';

/// Talks to `/api/admin/managed-applications`. Every call answers with the whole state, so the page
/// never has to work out what a change did.
class ManagedApplicationRepositoryImpl implements ManagedApplicationRepository {
  const ManagedApplicationRepositoryImpl(this._api);

  final ApiClient _api;

  static const _path = '/api/admin/managed-applications';

  @override
  Future<ManagedApplications> read() async =>
      managedApplicationsFromJson(await _api.getJson(_path) as Map<String, dynamic>);

  @override
  Future<ManagedApplications> mark({required List<String> names, required String owner}) async =>
      managedApplicationsFromJson(
        await _api.postJson(_path, body: {'names': names, 'owner': owner}) as Map<String, dynamic>,
      );

  @override
  Future<ManagedApplications> unmark(String applicationName) async => managedApplicationsFromJson(
        await _api.deleteJson(_path, query: {'name': applicationName}) as Map<String, dynamic>,
      );

  @override
  Future<ManagedApplications> addOwner(String name) async => managedApplicationsFromJson(
        await _api.postJson('$_path/owners', body: {'name': name}) as Map<String, dynamic>,
      );

  @override
  Future<ManagedApplications> removeOwner(String name) async => managedApplicationsFromJson(
        await _api.deleteJson('$_path/owners', query: {'name': name}) as Map<String, dynamic>,
      );
}
