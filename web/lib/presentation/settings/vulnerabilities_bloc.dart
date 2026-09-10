import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../domain/entities/vulnerability.dart';
import '../../domain/usecases/vulnerability_usecases.dart';
import 'settings_state.dart';

sealed class VulnerabilitySettingsEvent extends Equatable {
  const VulnerabilitySettingsEvent();

  @override
  List<Object?> get props => const [];
}

final class VulnerabilitySettingsRequested extends VulnerabilitySettingsEvent {
  const VulnerabilitySettingsRequested();
}

final class VulnerabilitySettingsSaveRequested extends VulnerabilitySettingsEvent {
  const VulnerabilitySettingsSaveRequested({
    required this.enabled,
    required this.nvdApiKey,
    required this.clearNvdApiKey,
    required this.syncIntervalHours,
    required this.assessmentsPerRun,
    required this.packagesPerRun,
    required this.autoSuggestCpes,
  });

  final bool enabled;

  /// Blank means "keep whatever is stored" — the form was never given the real key, so it has
  /// nothing to send back unchanged. [clearNvdApiKey] is how one is removed.
  final String? nvdApiKey;
  final bool clearNvdApiKey;

  final int? syncIntervalHours;
  final int? assessmentsPerRun;
  final int? packagesPerRun;
  final bool autoSuggestCpes;

  @override
  List<Object?> get props =>
      [enabled, nvdApiKey, clearNvdApiKey, syncIntervalHours, assessmentsPerRun, packagesPerRun, autoSuggestCpes];
}

final class VulnerabilitySettingsEdited extends VulnerabilitySettingsEvent {
  const VulnerabilitySettingsEdited();
}

final class VulnerabilityRunRequested extends VulnerabilitySettingsEvent {
  const VulnerabilityRunRequested();
}

final class VulnerabilityRunStatusRequested extends VulnerabilitySettingsEvent {
  const VulnerabilityRunStatusRequested();
}

class VulnerabilitySettingsState extends Equatable {
  const VulnerabilitySettingsState({
    this.settings = const SettingsState<VulnerabilitySettings>(),
    this.run = const VulnerabilityRunStatus(
      running: false,
      startedUtc: null,
      completedUtc: null,
      lastRunSucceeded: null,
      assessmentsCompleted: 0,
      assessmentsRemaining: 0,
      message: null,
    ),
    this.runError,
  });

  final SettingsState<VulnerabilitySettings> settings;
  final VulnerabilityRunStatus run;

  /// A failure starting a run — including the 409 that says one is already going. Kept apart from
  /// the settings error so a refused run does not look like a failed save.
  final String? runError;

  VulnerabilitySettingsState copyWith({
    SettingsState<VulnerabilitySettings>? settings,
    VulnerabilityRunStatus? run,
    String? runError,
    bool clearRunError = false,
  }) =>
      VulnerabilitySettingsState(
        settings: settings ?? this.settings,
        run: run ?? this.run,
        runError: clearRunError ? null : (runError ?? this.runError),
      );

  @override
  List<Object?> get props => [settings, run, runError];
}

class VulnerabilitySettingsBloc extends Bloc<VulnerabilitySettingsEvent, VulnerabilitySettingsState>
    with Polling<VulnerabilitySettingsEvent, VulnerabilitySettingsState> {
  VulnerabilitySettingsBloc({
    required GetVulnerabilitySettings getSettings,
    required UpdateVulnerabilitySettings updateSettings,
    required GetVulnerabilityRunStatus getRunStatus,
    required StartVulnerabilityRun startRun,
  })  : _getSettings = getSettings,
        _updateSettings = updateSettings,
        _getRunStatus = getRunStatus,
        _startRun = startRun,
        super(const VulnerabilitySettingsState()) {
    on<VulnerabilitySettingsRequested>(_onRequested);
    on<VulnerabilitySettingsSaveRequested>(_onSave);
    on<VulnerabilitySettingsEdited>((_, emit) => emit(state.copyWith(
          settings: state.settings.copyWith(saved: false, clearError: true, fieldErrors: const {}),
        )));
    on<VulnerabilityRunRequested>(_onRun);
    on<VulnerabilityRunStatusRequested>(_onRunStatus);
  }

  /// Slower than Vanta's two seconds, deliberately. A run here is minutes to hours rather than
  /// seconds — it is bounded by NVD's rate limit, not by how fast this server works — so a
  /// two-second poll would be several hundred pointless requests for one visible change.
  static const _pollInterval = Duration(seconds: 5);

  final GetVulnerabilitySettings _getSettings;
  final UpdateVulnerabilitySettings _updateSettings;
  final GetVulnerabilityRunStatus _getRunStatus;
  final StartVulnerabilityRun _startRun;

  Future<void> _onRequested(
      VulnerabilitySettingsRequested event, Emitter<VulnerabilitySettingsState> emit) async {
    emit(state.copyWith(settings: state.settings.copyWith(loading: true, clearError: true)));
    try {
      final settings = await _getSettings();
      final run = await _getRunStatus();
      emit(state.copyWith(
        settings: state.settings.copyWith(value: settings, loading: false),
        run: run,
      ));

      // A run started by the interval timer or by another administrator's browser is still worth
      // following, so polling keys off the server's answer rather than off this screen having
      // pressed the button.
      if (run.running) {
        startPolling(_pollInterval, const VulnerabilityRunStatusRequested());
      }
    } on ApiException catch (error) {
      emit(state.copyWith(settings: state.settings.copyWith(loading: false, error: error.message)));
    }
  }

  Future<void> _onSave(
      VulnerabilitySettingsSaveRequested event, Emitter<VulnerabilitySettingsState> emit) async {
    emit(state.copyWith(
      settings: state.settings.copyWith(saving: true, saved: false, clearError: true, fieldErrors: const {}),
    ));
    try {
      final saved = await _updateSettings(
        enabled: event.enabled,
        nvdApiKey: event.nvdApiKey,
        clearNvdApiKey: event.clearNvdApiKey,
        syncIntervalHours: event.syncIntervalHours,
        assessmentsPerRun: event.assessmentsPerRun,
        packagesPerRun: event.packagesPerRun,
        autoSuggestCpes: event.autoSuggestCpes,
      );
      emit(state.copyWith(settings: state.settings.copyWith(value: saved, saving: false, saved: true)));
    } on ApiException catch (error) {
      emit(state.copyWith(
        settings: state.settings.copyWith(
          saving: false,
          error: error.validationErrors.isEmpty ? error.message : null,
          fieldErrors: error.validationErrors,
        ),
      ));
    }
  }

  Future<void> _onRun(VulnerabilityRunRequested event, Emitter<VulnerabilitySettingsState> emit) async {
    emit(state.copyWith(clearRunError: true));
    try {
      emit(state.copyWith(run: await _startRun()));
      startPolling(_pollInterval, const VulnerabilityRunStatusRequested());
    } on ApiException catch (error) {
      // Includes the 409 for a run already in flight, which is a real constraint rather than
      // politeness: NVD counts requests per source address over a rolling window, so two
      // overlapping runs would spend one allowance between them and collect 403s.
      emit(state.copyWith(runError: error.message));
    }
  }

  Future<void> _onRunStatus(
      VulnerabilityRunStatusRequested event, Emitter<VulnerabilitySettingsState> emit) async {
    try {
      final run = await _getRunStatus();
      emit(state.copyWith(run: run));
      if (!run.running) {
        stopPolling();
      }
    } on ApiException catch (error) {
      // Stop rather than hammer a server that is answering with errors; the screen keeps the last
      // status it had and says why it stopped following.
      stopPolling();
      emit(state.copyWith(runError: error.message));
    }
  }
}
