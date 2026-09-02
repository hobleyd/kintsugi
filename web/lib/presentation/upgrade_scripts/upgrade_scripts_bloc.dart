import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/upgrade_script.dart';
import '../../domain/usecases/upgrade_script_usecases.dart';

sealed class UpgradeScriptsEvent extends Equatable {
  const UpgradeScriptsEvent();

  @override
  List<Object?> get props => const [];
}

final class UpgradeScriptsRequested extends UpgradeScriptsEvent {
  const UpgradeScriptsRequested();
}

final class UpgradeScriptsRefreshRequested extends UpgradeScriptsEvent {
  const UpgradeScriptsRefreshRequested();
}

/// Takes one approved script onto one local upgrade path — the last human decision before agents
/// run it as root.
final class ApprovedScriptAdoptRequested extends UpgradeScriptsEvent {
  const ApprovedScriptAdoptRequested(this.candidate);

  final AdoptionCandidate candidate;

  @override
  List<Object?> get props => [candidate];
}

/// Puts the script this build writes onto one package-manager row, unsigned.
final class ServerScriptTakeRequested extends UpgradeScriptsEvent {
  const ServerScriptTakeRequested(this.script);

  final LocalScript script;

  @override
  List<Object?> get props => [script];
}

final class UpgradeScriptsState extends Equatable {
  const UpgradeScriptsState({
    this.view,
    this.loading = true,
    this.busyLabel,
    this.error,
  });

  final UpgradeScriptsView? view;
  final bool loading;

  /// What is in flight, so the pressed button can say so and the others can be disabled. Each of
  /// these actions rewrites which rows are signed, so allowing two at once would leave the screen
  /// showing the outcome of whichever finished second.
  final String? busyLabel;

  final String? error;

  bool get busy => busyLabel != null;

  @override
  List<Object?> get props => [view, loading, busyLabel, error];
}

/// The Upgrade Scripts screen.
///
/// Every action returns the whole screen's state, because each one changes which rows are signed,
/// which have an upstream counterpart and which are still awaiting review — so there is one
/// response and one new state, rather than an outcome plus a follow-up read that could disagree
/// with it.
class UpgradeScriptsBloc extends Bloc<UpgradeScriptsEvent, UpgradeScriptsState> {
  UpgradeScriptsBloc({
    required GetUpgradeScriptsView getView,
    required RefreshApprovedScripts refreshApprovedScripts,
    required AdoptApprovedScript adoptApprovedScript,
    required TakeServerWrittenScript takeServerWrittenScript,
  })  : _getView = getView,
        _refreshApprovedScripts = refreshApprovedScripts,
        _adoptApprovedScript = adoptApprovedScript,
        _takeServerWrittenScript = takeServerWrittenScript,
        super(const UpgradeScriptsState()) {
    on<UpgradeScriptsRequested>(_onRequested);
    on<UpgradeScriptsRefreshRequested>(_onRefresh);
    on<ApprovedScriptAdoptRequested>(_onAdopt);
    on<ServerScriptTakeRequested>(_onTakeServerScript);
  }

  final GetUpgradeScriptsView _getView;
  final RefreshApprovedScripts _refreshApprovedScripts;
  final AdoptApprovedScript _adoptApprovedScript;
  final TakeServerWrittenScript _takeServerWrittenScript;

  Future<void> _onRequested(UpgradeScriptsRequested event, Emitter<UpgradeScriptsState> emit) async {
    emit(const UpgradeScriptsState(loading: true));
    try {
      emit(UpgradeScriptsState(view: await _getView(), loading: false));
    } on ApiException catch (error) {
      emit(UpgradeScriptsState(loading: false, error: error.message));
    }
  }

  Future<void> _onRefresh(
    UpgradeScriptsRefreshRequested event,
    Emitter<UpgradeScriptsState> emit,
  ) =>
      _run(emit, 'refresh', _refreshApprovedScripts.call);

  Future<void> _onAdopt(ApprovedScriptAdoptRequested event, Emitter<UpgradeScriptsState> emit) => _run(
        emit,
        'adopt:${event.candidate.applicationName}:${event.candidate.platform}',
        () => _adoptApprovedScript(
          applicationName: event.candidate.applicationName,
          platform: event.candidate.platform,
          sha256: event.candidate.sha256,
          signerFingerprint: event.candidate.signerFingerprint,
        ),
      );

  Future<void> _onTakeServerScript(
    ServerScriptTakeRequested event,
    Emitter<UpgradeScriptsState> emit,
  ) =>
      _run(
        emit,
        'take:${event.script.applicationName}:${event.script.platform}',
        () => _takeServerWrittenScript(
          applicationName: event.script.applicationName,
          platform: event.script.platform,
        ),
      );

  Future<void> _run(
    Emitter<UpgradeScriptsState> emit,
    String label,
    Future<UpgradeScriptsView> Function() action,
  ) async {
    emit(UpgradeScriptsState(view: state.view, loading: false, busyLabel: label));
    try {
      emit(UpgradeScriptsState(view: await action(), loading: false));
    } on ApiException catch (error) {
      // Only a failure of the request itself lands here. The server reports a GitHub outage, a
      // rejected entry or a refused adoption inside the response body, because none of those means
      // the request failed — and a reviewed script must keep patching the fleet it was reviewed
      // for whether or not GitHub is reachable.
      emit(UpgradeScriptsState(view: state.view, loading: false, error: error.message));
    }
  }
}
