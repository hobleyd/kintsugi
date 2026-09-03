import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'settings_state.dart';

sealed class VantaSettingsEvent extends Equatable {
  const VantaSettingsEvent();

  @override
  List<Object?> get props => const [];
}

final class VantaSettingsRequested extends VantaSettingsEvent {
  const VantaSettingsRequested();
}

final class VantaSettingsSaveRequested extends VantaSettingsEvent {
  const VantaSettingsSaveRequested({
    required this.enabled,
    required this.clientId,
    required this.clientSecret,
    required this.clearClientSecret,
    required this.apiBaseUrl,
    required this.vulnerableComponentResourceId,
    required this.packageVulnerabilityResourceId,
    required this.consoleBaseUrl,
    required this.severity,
    required this.syncIntervalHours,
  });

  final bool enabled;
  final String? clientId;

  /// Blank means "keep whatever is stored" — the form was never given the real secret, so it has
  /// nothing to send back unchanged. [clearClientSecret] is how one is removed.
  final String? clientSecret;
  final bool clearClientSecret;

  final String? apiBaseUrl;
  final String? vulnerableComponentResourceId;
  final String? packageVulnerabilityResourceId;
  final String? consoleBaseUrl;
  final double? severity;
  final int? syncIntervalHours;

  @override
  List<Object?> get props => [
        enabled,
        clientId,
        clientSecret,
        clearClientSecret,
        apiBaseUrl,
        vulnerableComponentResourceId,
        packageVulnerabilityResourceId,
        consoleBaseUrl,
        severity,
        syncIntervalHours,
      ];
}

final class VantaSettingsEdited extends VantaSettingsEvent {
  const VantaSettingsEdited();
}

final class VantaSyncRequested extends VantaSettingsEvent {
  const VantaSyncRequested();
}

final class VantaSyncStatusRequested extends VantaSettingsEvent {
  const VantaSyncStatusRequested();
}

/// The Vanta settings screen's state: the shared settings shape, plus the background sync's own
/// status, which this screen polls while a run is in flight.
class VantaState extends Equatable {
  const VantaState({
    this.settings = const SettingsState<VantaSettings>(),
    this.sync = const VantaSyncStatus.unknown(),
    this.syncError,
  });

  final SettingsState<VantaSettings> settings;
  final VantaSyncStatus sync;

  /// A failure starting a sync — including the 409 that says one is already running. Kept apart
  /// from the settings error so a failed sync does not look like a failed save.
  final String? syncError;

  VantaState copyWith({
    SettingsState<VantaSettings>? settings,
    VantaSyncStatus? sync,
    String? syncError,
    bool clearSyncError = false,
  }) =>
      VantaState(
        settings: settings ?? this.settings,
        sync: sync ?? this.sync,
        syncError: clearSyncError ? null : (syncError ?? this.syncError),
      );

  @override
  List<Object?> get props => [settings, sync, syncError];
}

class VantaSettingsBloc extends Bloc<VantaSettingsEvent, VantaState>
    with Polling<VantaSettingsEvent, VantaState> {
  VantaSettingsBloc({
    required GetVantaSettings getSettings,
    required UpdateVantaSettings updateSettings,
    required GetVantaSyncStatus getSyncStatus,
    required StartVantaSync startSync,
  })  : _getSettings = getSettings,
        _updateSettings = updateSettings,
        _getSyncStatus = getSyncStatus,
        _startSync = startSync,
        super(const VantaState()) {
    on<VantaSettingsRequested>(_onRequested);
    on<VantaSettingsSaveRequested>(_onSave);
    on<VantaSettingsEdited>((_, emit) => emit(state.copyWith(
          settings: state.settings.copyWith(saved: false, clearError: true, fieldErrors: const {}),
        )));
    on<VantaSyncRequested>(_onSync);
    on<VantaSyncStatusRequested>(_onSyncStatus);
  }

  /// While a run is in flight. A fleet-wide sync is two HTTP requests to Vanta, so this is about
  /// watching a thing that takes seconds rather than minutes.
  static const _pollInterval = Duration(seconds: 2);

  final GetVantaSettings _getSettings;
  final UpdateVantaSettings _updateSettings;
  final GetVantaSyncStatus _getSyncStatus;
  final StartVantaSync _startSync;

  Future<void> _onRequested(VantaSettingsRequested event, Emitter<VantaState> emit) async {
    emit(state.copyWith(settings: state.settings.copyWith(loading: true, clearError: true)));
    try {
      final settings = await _getSettings();
      final sync = await _getSyncStatus();
      emit(state.copyWith(
        settings: state.settings.copyWith(value: settings, loading: false),
        sync: sync,
      ));

      // A run started by something else — the interval timer, or another administrator's browser —
      // is still worth following, so polling keys off the server's answer rather than off this
      // screen having been the one to press the button.
      if (sync.running) {
        startPolling(_pollInterval, const VantaSyncStatusRequested());
      }
    } on ApiException catch (error) {
      emit(state.copyWith(settings: state.settings.copyWith(loading: false, error: error.message)));
    }
  }

  Future<void> _onSave(VantaSettingsSaveRequested event, Emitter<VantaState> emit) async {
    emit(state.copyWith(
      settings: state.settings.copyWith(saving: true, saved: false, clearError: true, fieldErrors: const {}),
    ));
    try {
      final saved = await _updateSettings(
        enabled: event.enabled,
        clientId: event.clientId,
        clientSecret: event.clientSecret,
        clearClientSecret: event.clearClientSecret,
        apiBaseUrl: event.apiBaseUrl,
        vulnerableComponentResourceId: event.vulnerableComponentResourceId,
        packageVulnerabilityResourceId: event.packageVulnerabilityResourceId,
        consoleBaseUrl: event.consoleBaseUrl,
        severity: event.severity,
        syncIntervalHours: event.syncIntervalHours,
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

  Future<void> _onSync(VantaSyncRequested event, Emitter<VantaState> emit) async {
    emit(state.copyWith(clearSyncError: true));
    try {
      emit(state.copyWith(sync: await _startSync()));
      startPolling(_pollInterval, const VantaSyncStatusRequested());
    } on ApiException catch (error) {
      // Includes the 409 for a run already in flight, which is a real constraint rather than
      // politeness: Vanta revokes an application's previous access token whenever a new one is
      // issued, so two overlapping runs would invalidate each other mid-upload.
      emit(state.copyWith(syncError: error.message));
    }
  }

  Future<void> _onSyncStatus(VantaSyncStatusRequested event, Emitter<VantaState> emit) async {
    try {
      final sync = await _getSyncStatus();
      emit(state.copyWith(sync: sync));
      if (!sync.running) {
        stopPolling();
      }
    } on ApiException catch (error) {
      // Stop rather than hammer a server that is answering with errors; the screen keeps the last
      // status it had and says why it stopped following.
      stopPolling();
      emit(state.copyWith(syncError: error.message));
    }
  }
}
