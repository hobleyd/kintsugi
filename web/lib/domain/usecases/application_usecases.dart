import '../entities/application.dart';
import '../repositories/repositories.dart';

class GetApplicationOverview {
  const GetApplicationOverview(this._repository);

  final ApplicationRepository _repository;

  Future<ApplicationOverview> call() => _repository.overview();
}
