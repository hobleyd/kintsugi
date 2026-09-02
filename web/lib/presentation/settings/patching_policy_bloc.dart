import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'settings_state.dart';

sealed class PatchingPolicyEvent extends Equatable {
  const PatchingPolicyEvent();

  @override
  List<Object?> get props => const [];
}

final class PatchingPolicyRequested extends PatchingPolicyEvent {
  const PatchingPolicyRequested();
}

final class PatchingPolicySaveRequested extends PatchingPolicyEvent {
  const PatchingPolicySaveRequested(this.settings);

  final PatchingPolicySettings settings;

  @override
  List<Object?> get props => [settings];
}

/// Clears the "saved" confirmation, dispatched as soon as the form is edited so the message does
/// not sit there over changes that have not been saved.
final class PatchingPolicyEdited extends PatchingPolicyEvent {
  const PatchingPolicyEdited();
}

class PatchingPolicyBloc extends Bloc<PatchingPolicyEvent, SettingsState<PatchingPolicySettings>> {
  PatchingPolicyBloc({
    required GetPatchingPolicySettings getSettings,
    required UpdatePatchingPolicySettings updateSettings,
  })  : _getSettings = getSettings,
        _updateSettings = updateSettings,
        super(const SettingsState()) {
    on<PatchingPolicyRequested>(_onRequested);
    on<PatchingPolicySaveRequested>(_onSave);
    on<PatchingPolicyEdited>(
      (_, emit) => emit(state.copyWith(saved: false, clearError: true, fieldErrors: const {})),
    );
  }

  final GetPatchingPolicySettings _getSettings;
  final UpdatePatchingPolicySettings _updateSettings;

  Future<void> _onRequested(
    PatchingPolicyRequested event,
    Emitter<SettingsState<PatchingPolicySettings>> emit,
  ) async {
    emit(state.copyWith(loading: true, clearError: true));
    try {
      emit(state.copyWith(value: await _getSettings(), loading: false));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onSave(
    PatchingPolicySaveRequested event,
    Emitter<SettingsState<PatchingPolicySettings>> emit,
  ) async {
    emit(state.copyWith(saving: true, saved: false, clearError: true, fieldErrors: const {}));
    try {
      emit(state.copyWith(value: await _updateSettings(event.settings), saving: false, saved: true));
    } on ApiException catch (error) {
      emit(state.copyWith(
        saving: false,
        error: error.validationErrors.isEmpty ? error.message : null,
        fieldErrors: error.validationErrors,
      ));
    }
  }
}
