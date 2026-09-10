import '../entities/patch_failure.dart';
import '../repositories/repositories.dart';

/// Every reported patch failure, fleet-wide — outstanding first.
class GetPatchFailures {
  const GetPatchFailures(this._repository);

  final PatchFailureRepository _repository;

  Future<List<PatchFailure>> call() => _repository.list();
}

/// Clears one failure off the screen by hand, for one that cannot recur.
class DismissPatchFailure {
  const DismissPatchFailure(this._repository);

  final PatchFailureRepository _repository;

  Future<void> call(String id) => _repository.dismiss(id);
}
