import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';

sealed class AdministratorsEvent extends Equatable {
  const AdministratorsEvent();

  @override
  List<Object?> get props => const [];
}

final class AdministratorsRequested extends AdministratorsEvent {
  const AdministratorsRequested();
}

/// [emails] are already split, trimmed and stripped of display names (`parseAdminEmails`).
final class AdministratorsAddRequested extends AdministratorsEvent {
  const AdministratorsAddRequested(this.emails);

  final List<String> emails;

  @override
  List<Object?> get props => [emails];
}

final class AdministratorsRemoveRequested extends AdministratorsEvent {
  const AdministratorsRemoveRequested(this.email);

  final String email;

  @override
  List<Object?> get props => [email];
}

/// The Administrators page's state. Not [SettingsState]: that one is shaped around a form that is
/// loaded and then saved whole, where this page changes one entry at a time and each answer is the
/// whole list as it now stands.
class AdministratorsState extends Equatable {
  const AdministratorsState({
    this.administrators,
    this.loading = true,
    this.busy = false,
    this.error,
    this.notice,
  });

  /// Null until the first read succeeds, so a failed load is not mistaken for "nobody is an
  /// administrator" — which would show the warning that every account has access.
  final List<Administrator>? administrators;
  final bool loading;

  /// An add or a delete is in flight.
  final bool busy;
  final String? error;

  /// What the last change did, for a confirmation; cleared when the next one starts.
  final String? notice;

  AdministratorsState copyWith({
    List<Administrator>? administrators,
    bool? loading,
    bool? busy,
    String? error,
    String? notice,
    bool clearError = false,
    bool clearNotice = false,
  }) =>
      AdministratorsState(
        administrators: administrators ?? this.administrators,
        loading: loading ?? this.loading,
        busy: busy ?? this.busy,
        error: clearError ? null : (error ?? this.error),
        notice: clearNotice ? null : (notice ?? this.notice),
      );

  @override
  List<Object?> get props => [administrators, loading, busy, error, notice];
}

class AdministratorsBloc extends Bloc<AdministratorsEvent, AdministratorsState> {
  AdministratorsBloc({
    required GetAdministrators getAdministrators,
    required AddAdministrators addAdministrators,
    required RemoveAdministrator removeAdministrator,
  })  : _get = getAdministrators,
        _add = addAdministrators,
        _remove = removeAdministrator,
        super(const AdministratorsState()) {
    on<AdministratorsRequested>(_onRequested);
    on<AdministratorsAddRequested>(_onAdd);
    on<AdministratorsRemoveRequested>(_onRemove);
  }

  final GetAdministrators _get;
  final AddAdministrators _add;
  final RemoveAdministrator _remove;

  Future<void> _onRequested(AdministratorsRequested event, Emitter<AdministratorsState> emit) async {
    emit(state.copyWith(loading: true, clearError: true));
    try {
      emit(state.copyWith(administrators: await _get(), loading: false));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onAdd(AdministratorsAddRequested event, Emitter<AdministratorsState> emit) async {
    emit(state.copyWith(busy: true, clearError: true, clearNotice: true));
    try {
      final before = state.administrators?.map((a) => a.email.toLowerCase()).toSet() ?? <String>{};
      final updated = await _add(event.emails);
      final added = updated.where((a) => !before.contains(a.email.toLowerCase())).length;
      emit(state.copyWith(
        administrators: updated,
        busy: false,
        notice: added == 0
            ? 'Already an administrator.'
            : 'Added $added administrator${added == 1 ? '' : 's'}.',
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(busy: false, error: _messageOf(error)));
    }
  }

  Future<void> _onRemove(AdministratorsRemoveRequested event, Emitter<AdministratorsState> emit) async {
    emit(state.copyWith(busy: true, clearError: true, clearNotice: true));
    try {
      emit(state.copyWith(
        administrators: await _remove(event.email),
        busy: false,
        notice: 'Deleted ${event.email}.',
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(busy: false, error: _messageOf(error)));
    }
  }

  /// A validation failure names the entry under `Emails[0]`, `Emails[1]`, … — keys that mean nothing
  /// to somebody who typed one box — so their messages are shown together, once each.
  static String _messageOf(ApiException error) {
    final messages = <String>{
      for (final entry in error.validationErrors.values) ...entry,
    };
    return messages.isEmpty ? error.message : messages.join(' ');
  }
}
