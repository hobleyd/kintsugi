import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'settings_state.dart';

sealed class GitHubSettingsEvent extends Equatable {
  const GitHubSettingsEvent();

  @override
  List<Object?> get props => const [];
}

final class GitHubSettingsRequested extends GitHubSettingsEvent {
  const GitHubSettingsRequested();
}

final class GitHubSettingsSaveRequested extends GitHubSettingsEvent {
  const GitHubSettingsSaveRequested({
    required this.agentPackageRepository,
    required this.scriptApprovalRepository,
    required this.apiToken,
    required this.clearApiToken,
    required this.scriptApprovalToken,
    required this.clearScriptApprovalToken,
  });

  final String? agentPackageRepository;
  final String? scriptApprovalRepository;

  /// Blank means "keep whatever is stored" — the form was never given the real value, so it has
  /// nothing to send back unchanged. The `clear` flags are how one is removed, since blank cannot
  /// mean both.
  final String? apiToken;
  final bool clearApiToken;
  final String? scriptApprovalToken;
  final bool clearScriptApprovalToken;

  @override
  List<Object?> get props => [
        agentPackageRepository,
        scriptApprovalRepository,
        apiToken,
        clearApiToken,
        scriptApprovalToken,
        clearScriptApprovalToken,
      ];
}

final class GitHubSettingsEdited extends GitHubSettingsEvent {
  const GitHubSettingsEdited();
}

class GitHubSettingsBloc extends Bloc<GitHubSettingsEvent, SettingsState<GitHubSettings>> {
  GitHubSettingsBloc({
    required GetGitHubSettings getSettings,
    required UpdateGitHubSettings updateSettings,
  })  : _getSettings = getSettings,
        _updateSettings = updateSettings,
        super(const SettingsState()) {
    on<GitHubSettingsRequested>(_onRequested);
    on<GitHubSettingsSaveRequested>(_onSave);
    on<GitHubSettingsEdited>(
      (_, emit) => emit(state.copyWith(saved: false, clearError: true, fieldErrors: const {})),
    );
  }

  final GetGitHubSettings _getSettings;
  final UpdateGitHubSettings _updateSettings;

  Future<void> _onRequested(
    GitHubSettingsRequested event,
    Emitter<SettingsState<GitHubSettings>> emit,
  ) async {
    emit(state.copyWith(loading: true, clearError: true));
    try {
      emit(state.copyWith(value: await _getSettings(), loading: false));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onSave(
    GitHubSettingsSaveRequested event,
    Emitter<SettingsState<GitHubSettings>> emit,
  ) async {
    emit(state.copyWith(saving: true, saved: false, clearError: true, fieldErrors: const {}));
    try {
      final saved = await _updateSettings(
        agentPackageRepository: event.agentPackageRepository,
        scriptApprovalRepository: event.scriptApprovalRepository,
        apiToken: event.apiToken,
        clearApiToken: event.clearApiToken,
        scriptApprovalToken: event.scriptApprovalToken,
        clearScriptApprovalToken: event.clearScriptApprovalToken,
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
