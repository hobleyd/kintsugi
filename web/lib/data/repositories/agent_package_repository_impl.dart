import '../../core/network/api_client.dart';
import '../../domain/entities/agent_package.dart';
import '../../domain/repositories/repositories.dart';
import '../models/agent_package_mapper.dart';

class AgentPackageRepositoryImpl implements AgentPackageRepository {
  const AgentPackageRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<ClientsView> view() async =>
      clientsViewFromJson(await _api.getJson('/api/admin/clients') as Map<String, dynamic>);

  /// Both calls return the whole screen's state, so a refresh needs no follow-up read — and there
  /// is no window in which the import results are shown beside the packages they replaced.
  @override
  Future<ClientsView> refresh() async => clientsViewFromJson(
        await _api.postJson('/api/admin/clients/refresh') as Map<String, dynamic>,
      );

  @override
  Future<WindowsBootstrapScript> windowsBootstrapScript() async => windowsBootstrapScriptFromJson(
        await _api.getJson('/api/admin/clients/windows/bootstrap-script') as Map<String, dynamic>,
      );

  /// Where a package is downloaded from.
  ///
  /// Reachable without a session on the server side: an already-enrolled agent's self-update has
  /// to be able to see what is published before it has proven anything, and the download is
  /// protected by a signed checksum instead. But the enrollment token is written into the archive
  /// only for a signed-in administrator — anyone else gets the package as published, with a blank
  /// token (see AgentPackagesController.Download). Exposed as a URL rather than fetched, because
  /// the browser should be doing the downloading; a same-origin navigation carries the session
  /// cookie, which is what makes the downloaded package able to enroll.
  static String downloadUrl(String platform) =>
      '/api/agent-packages/${Uri.encodeComponent(platform)}/download';
}
