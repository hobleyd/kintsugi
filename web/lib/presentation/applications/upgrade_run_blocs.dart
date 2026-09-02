import '../../domain/usecases/upgrade_path_usecases.dart';
import 'background_run_bloc.dart';
import 'run_progress_mappers.dart';

/// "Find Upgrade Paths": resolves an update method for every installed application that has not
/// got one yet, one at a time, in series.
///
/// A named subclass rather than a second [BackgroundRunBloc] instance because the widget tree looks
/// blocs up by type, and two providers of the same type would resolve to whichever is nearest. The
/// class exists for that reason alone; the behaviour is entirely the base's.
class UpgradePathScanBloc extends BackgroundRunBloc {
  UpgradePathScanBloc({
    required StartUpgradePathScan startScan,
    required GetUpgradePathScanStatus scanStatus,
  }) : super(
          start: () async => startedProgress(await startScan(), scanProgress),
          status: () async => scanProgress(await scanStatus()),
        );
}

/// "Check for Updates": re-runs each already-resolved script's own `--update-version` to see
/// whether a newer version has been released. No AI call is involved, which is why it is a
/// separate run from the scan above with its own progress.
class UpdateCheckBloc extends BackgroundRunBloc {
  UpdateCheckBloc({
    required StartUpdateCheck startUpdateCheck,
    required GetUpdateCheckStatus updateCheckStatus,
  }) : super(
          start: () async => startedProgress(await startUpdateCheck(), updateCheckProgress),
          status: () async => updateCheckProgress(await updateCheckStatus()),
        );
}
