import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'settings_state.dart';

sealed class AuditingSettingsEvent extends Equatable {
  const AuditingSettingsEvent();

  @override
  List<Object?> get props => const [];
}

final class AuditingSettingsRequested extends AuditingSettingsEvent {
  const AuditingSettingsRequested();
}

final class AuditingSettingsSaveRequested extends AuditingSettingsEvent {
  const AuditingSettingsSaveRequested({
    required this.provider,
    required this.isEnabled,
    required this.endpoint,
    required this.region,
    required this.clientId,
    required this.secret,
    required this.clearSecret,
    required this.tenantId,
    required this.projectId,
    required this.logGroup,
    required this.dataCollectionRuleId,
    required this.stream,
    required this.index,
  });

  final AuditProvider provider;
  final bool isEnabled;
  final String? endpoint;
  final String? region;
  final String? clientId;

  /// Blank means "keep the stored secret" — for the same provider. The server drops the stored
  /// secret on a provider change regardless, so switching to a provider that requires one means
  /// entering it; the form says so beside the field.
  final String? secret;

  final bool clearSecret;
  final String? tenantId;
  final String? projectId;
  final String? logGroup;
  final String? dataCollectionRuleId;
  final String? stream;
  final String? index;

  @override
  List<Object?> get props => [
        provider,
        isEnabled,
        endpoint,
        region,
        clientId,
        secret,
        clearSecret,
        tenantId,
        projectId,
        logGroup,
        dataCollectionRuleId,
        stream,
        index,
      ];
}

final class AuditingSettingsEdited extends AuditingSettingsEvent {
  const AuditingSettingsEdited();
}

class AuditingSettingsBloc extends Bloc<AuditingSettingsEvent, SettingsState<AuditSettings>> {
  AuditingSettingsBloc({
    required GetAuditSettings getSettings,
    required UpdateAuditSettings updateSettings,
  })  : _getSettings = getSettings,
        _updateSettings = updateSettings,
        super(const SettingsState()) {
    on<AuditingSettingsRequested>(_onRequested);
    on<AuditingSettingsSaveRequested>(_onSave);
    on<AuditingSettingsEdited>(
      (_, emit) => emit(state.copyWith(saved: false, clearError: true, fieldErrors: const {})),
    );
  }

  final GetAuditSettings _getSettings;
  final UpdateAuditSettings _updateSettings;

  Future<void> _onRequested(
    AuditingSettingsRequested event,
    Emitter<SettingsState<AuditSettings>> emit,
  ) async {
    emit(state.copyWith(loading: true, clearError: true));
    try {
      emit(state.copyWith(value: await _getSettings(), loading: false));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onSave(
    AuditingSettingsSaveRequested event,
    Emitter<SettingsState<AuditSettings>> emit,
  ) async {
    emit(state.copyWith(saving: true, saved: false, clearError: true, fieldErrors: const {}));
    try {
      final saved = await _updateSettings(
        provider: event.provider,
        isEnabled: event.isEnabled,
        endpoint: event.endpoint,
        region: event.region,
        clientId: event.clientId,
        secret: event.secret,
        clearSecret: event.clearSecret,
        tenantId: event.tenantId,
        projectId: event.projectId,
        logGroup: event.logGroup,
        dataCollectionRuleId: event.dataCollectionRuleId,
        stream: event.stream,
        index: event.index,
      );
      emit(state.copyWith(value: saved, saving: false, saved: true));
    } on ApiException catch (error) {
      emit(state.copyWith(
        saving: false,
        error: error.validationErrors.isEmpty ? error.message : null,
        fieldErrors: error.validationErrors,
      ));
    }
  }
}
