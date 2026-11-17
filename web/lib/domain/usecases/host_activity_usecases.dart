import '../entities/host_activity.dart';
import '../repositories/repositories.dart';

/// One host's timeline, pending updates and open failures.
class GetHostActivity {
  const GetHostActivity(this._repository);

  final HostActivityRepository _repository;

  Future<HostActivity> call(String hostId) => _repository.get(hostId);
}

/// Sets how many months of host activity are kept. Returns what the server stored.
class SetHostActivityRetention {
  const SetHostActivityRetention(this._repository);

  final HostActivityRepository _repository;

  Future<int> call(int months) => _repository.setRetention(months);
}
