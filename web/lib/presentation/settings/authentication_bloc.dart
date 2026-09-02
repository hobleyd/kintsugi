import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'settings_state.dart';

sealed class AuthenticationSettingsEvent extends Equatable {
  const AuthenticationSettingsEvent();

  @override
  List<Object?> get props => const [];
}

final class AuthenticationSettingsRequested extends AuthenticationSettingsEvent {
  const AuthenticationSettingsRequested();
}

final class AuthenticationSettingsSaveRequested extends AuthenticationSettingsEvent {
  const AuthenticationSettingsSaveRequested({
    required this.provider,
    required this.clientId,
    required this.clientSecret,
    required this.authority,
    required this.tenantId,
    required this.hostedDomain,
    required this.isEnabled,
  });

  final AuthProvider provider;
  final String? clientId;

  /// Blank means "keep the stored secret". The domain refuses to save a row without one, so this
  /// is the only way to change the provider on an already-configured server without re-entering
  /// the secret.
  final String? clientSecret;

  final String? authority;
  final String? tenantId;
  final String? hostedDomain;
  final bool isEnabled;

  @override
  List<Object?> get props =>
      [provider, clientId, clientSecret, authority, tenantId, hostedDomain, isEnabled];
}

final class AuthenticationSettingsEdited extends AuthenticationSettingsEvent {
  const AuthenticationSettingsEdited();
}

class AuthenticationSettingsBloc
    extends Bloc<AuthenticationSettingsEvent, SettingsState<AuthenticationSettings>> {
  AuthenticationSettingsBloc({
    required GetAuthenticationSettings getSettings,
    required UpdateAuthenticationSettings updateSettings,
  })  : _getSettings = getSettings,
        _updateSettings = updateSettings,
        super(const SettingsState()) {
    on<AuthenticationSettingsRequested>(_onRequested);
    on<AuthenticationSettingsSaveRequested>(_onSave);
    on<AuthenticationSettingsEdited>(
      (_, emit) => emit(state.copyWith(saved: false, clearError: true, fieldErrors: const {})),
    );
  }

  final GetAuthenticationSettings _getSettings;
  final UpdateAuthenticationSettings _updateSettings;

  Future<void> _onRequested(
    AuthenticationSettingsRequested event,
    Emitter<SettingsState<AuthenticationSettings>> emit,
  ) async {
    emit(state.copyWith(loading: true, clearError: true));
    try {
      emit(state.copyWith(value: await _getSettings(), loading: false));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onSave(
    AuthenticationSettingsSaveRequested event,
    Emitter<SettingsState<AuthenticationSettings>> emit,
  ) async {
    emit(state.copyWith(saving: true, saved: false, clearError: true, fieldErrors: const {}));
    try {
      final saved = await _updateSettings(
        provider: event.provider,
        clientId: event.clientId,
        clientSecret: event.clientSecret,
        authority: event.authority,
        tenantId: event.tenantId,
        hostedDomain: event.hostedDomain,
        isEnabled: event.isEnabled,
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
