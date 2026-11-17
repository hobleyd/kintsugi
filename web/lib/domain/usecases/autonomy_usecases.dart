import '../entities/autonomy.dart';
import '../repositories/repositories.dart';

/// The Autonomy page's data for one period.
class GetAutonomy {
  const GetAutonomy(this._repository);

  final AutonomyRepository _repository;

  Future<Autonomy> call(AutonomyPeriod period, {String? month}) => _repository.get(period, month: month);
}

/// A page of the devices held up by one cause, so a fleet of any size is read a page at a time.
class GetAutonomyCauseDevices {
  const GetAutonomyCauseDevices(this._repository);

  final AutonomyRepository _repository;

  Future<AutonomyDevicesPage> call(String key, {int skip = 0, int take = 25, String? search}) =>
      _repository.devices(key, skip: skip, take: take, search: search);
}

/// Patch now for every Windows device in one cause. The server resolves the devices, so the browser
/// sends only how many it was shown: a different count is refused.
class PatchAutonomyCause {
  const PatchAutonomyCause(this._repository);

  final AutonomyRepository _repository;

  Future<AutonomyPatchNowResult> call(String key, {required int expectedConnected, required bool includeOffline}) =>
      _repository.patchNow(key, expectedConnected: expectedConnected, includeOffline: includeOffline);
}
