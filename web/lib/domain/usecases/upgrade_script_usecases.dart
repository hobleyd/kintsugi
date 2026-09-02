import '../entities/upgrade_script.dart';
import '../repositories/repositories.dart';

class GetUpgradeScriptsView {
  const GetUpgradeScriptsView(this._repository);

  final UpgradeScriptRepository _repository;

  Future<UpgradeScriptsView> call() => _repository.view();
}

/// Reads the approval repository's default branch back and signs any local script whose bytes are
/// already approved there.
class RefreshApprovedScripts {
  const RefreshApprovedScripts(this._repository);

  final UpgradeScriptRepository _repository;

  Future<UpgradeScriptsView> call() => _repository.refresh();
}

/// Takes one approved script onto one local upgrade path.
///
/// Per-row and human-pressed, deliberately. A merge to the approval repository's default branch is
/// enough to *offer* new executable content to every server that refreshes, so this is the last
/// human decision before agents run it as root — which is also why it refuses a row that already
/// carries a signature: agents may be running that one now.
class AdoptApprovedScript {
  const AdoptApprovedScript(this._repository);

  final UpgradeScriptRepository _repository;

  Future<UpgradeScriptsView> call({
    required String applicationName,
    required String platform,
    required String sha256,
    required String signerFingerprint,
  }) =>
      _repository.adopt(
        applicationName: applicationName,
        platform: platform,
        sha256: sha256,
        signerFingerprint: signerFingerprint,
      );
}

/// Puts the script this build writes onto one package-manager row, unsigned.
class TakeServerWrittenScript {
  const TakeServerWrittenScript(this._repository);

  final UpgradeScriptRepository _repository;

  Future<UpgradeScriptsView> call({required String applicationName, required String platform}) =>
      _repository.takeServerScript(applicationName: applicationName, platform: platform);
}
