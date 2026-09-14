import '../entities/application.dart';
import '../repositories/repositories.dart';

class GetApplicationOverview {
  const GetApplicationOverview(this._repository);

  final ApplicationRepository _repository;

  Future<ApplicationOverview> call() => _repository.overview();
}

/// The Applications screen's "Patch now" action — see [ApplicationRepository.forcePatchRuns].
class RequestForcedPatchRuns {
  const RequestForcedPatchRuns(this._repository);

  final ApplicationRepository _repository;

  Future<ForcedPatchRunResult> call({
    required String applicationName,
    required String platform,
    required List<String> hostNames,
  }) =>
      _repository.forcePatchRuns(
        applicationName: applicationName,
        platform: platform,
        hostNames: hostNames,
      );
}
