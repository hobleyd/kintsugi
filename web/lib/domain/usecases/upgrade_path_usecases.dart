import '../entities/upgrade_path.dart';
import '../repositories/repositories.dart';

/// Starts the fleet-wide "Find Upgrade Paths" run.
class StartUpgradePathScan {
  const StartUpgradePathScan(this._repository);

  final UpgradePathRepository _repository;

  Future<RunStarted<UpgradePathScanStatus>> call() => _repository.startScan();
}

class GetUpgradePathScanStatus {
  const GetUpgradePathScanStatus(this._repository);

  final UpgradePathRepository _repository;

  Future<UpgradePathScanStatus> call() => _repository.scanStatus();
}

/// Starts "Check for Updates", which re-runs each resolved script's own `--update-version` and
/// makes no AI call.
class StartUpdateCheck {
  const StartUpdateCheck(this._repository);

  final UpgradePathRepository _repository;

  Future<RunStarted<UpdateCheckStatus>> call() => _repository.startUpdateCheck();
}

class GetUpdateCheckStatus {
  const GetUpdateCheckStatus(this._repository);

  final UpgradePathRepository _repository;

  Future<UpdateCheckStatus> call() => _repository.updateCheckStatus();
}

/// Re-checks one row's version by running its own script — the per-row form of [StartUpdateCheck],
/// and like it, no AI call.
class CheckApplicationUpdate {
  const CheckApplicationUpdate(this._repository);

  final UpgradePathRepository _repository;

  Future<UpdateCheckResult> call({required String applicationName, required String platform}) =>
      _repository.checkUpdate(applicationName: applicationName, platform: platform);
}

class GetUpgradePathPrompt {
  const GetUpgradePathPrompt(this._repository);

  final UpgradePathRepository _repository;

  Future<UpgradePathPrompt> call({required String applicationName, String? platform}) =>
      _repository.prompt(applicationName: applicationName, platform: platform);
}

/// Sends one application's instructions to the AI agent.
class StartUpgradePathRefresh {
  const StartUpgradePathRefresh(this._repository);

  final UpgradePathRepository _repository;

  Future<RunStarted<UpgradePathRefreshStatus>> call({
    required String applicationName,
    String? platform,
    String? instructions,
  }) =>
      _repository.startRefresh(
        applicationName: applicationName,
        platform: platform,
        instructions: instructions,
      );
}

class GetUpgradePathRefreshStatus {
  const GetUpgradePathRefreshStatus(this._repository);

  final UpgradePathRepository _repository;

  Future<UpgradePathRefreshStatus> call(String applicationName) =>
      _repository.refreshStatus(applicationName);
}

/// Saves an upgrade path from what is in the editor, bypassing the AI.
///
/// This is the one use case that carries a raw JSON map rather than an entity, and it earns the
/// exception: the editor accepts either a whole result envelope or a bare replacement script, and
/// whichever it was has to reach the server with every field it did not carry itself left intact.
class SaveUpgradePath {
  const SaveUpgradePath(this._repository);

  final UpgradePathRepository _repository;

  Future<UpgradePathResult> call(Map<String, dynamic> body) => _repository.save(body);
}

/// Signs the stored script for one row, so agents will run it.
///
/// The last human step before the fleet executes content as root. It signs what is *already
/// stored*, never what the client currently has on screen — which is why the Applications screen
/// disables it the moment the editor is touched, and re-enables it only once a save or a fresh AI
/// result has brought the two back into sync.
class SignUpgradePathScript {
  const SignUpgradePathScript(this._repository);

  final UpgradePathRepository _repository;

  Future<UpgradePathResult> call({required String applicationName, required String platform}) =>
      _repository.signScript(applicationName: applicationName, platform: platform);
}
