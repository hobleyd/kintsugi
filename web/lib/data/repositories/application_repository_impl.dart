import '../../core/network/api_client.dart';
import '../../domain/entities/application.dart';
import '../../domain/repositories/repositories.dart';
import '../models/application_mapper.dart';

class ApplicationRepositoryImpl implements ApplicationRepository {
  const ApplicationRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<ApplicationOverview> overview() async => applicationOverviewFromJson(
        await _api.getJson('/api/admin/applications') as Map<String, dynamic>,
      );
}
