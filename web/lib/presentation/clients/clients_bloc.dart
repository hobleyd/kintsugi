import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/agent_package.dart';
import '../../domain/usecases/client_usecases.dart';

sealed class ClientsEvent extends Equatable {
  const ClientsEvent();

  @override
  List<Object?> get props => const [];
}

final class ClientsRequested extends ClientsEvent {
  const ClientsRequested();
}

final class ClientsRefreshRequested extends ClientsEvent {
  const ClientsRefreshRequested();
}

/// Opens the release-notes panel under one platform's row, or closes it if it is already open.
final class ClientsRowExpansionToggled extends ClientsEvent {
  const ClientsRowExpansionToggled(this.platform);

  final String platform;

  @override
  List<Object?> get props => [platform];
}

final class ClientsState extends Equatable {
  const ClientsState({
    this.view,
    this.loading = true,
    this.refreshing = false,
    this.error,
    this.expandedPlatform,
  });

  final ClientsView? view;
  final bool loading;
  final bool refreshing;

  /// A failure of the request itself. Distinct from `view.refreshError`, which is the server
  /// reporting that it could not list the upstream releases — that one arrives on a successful
  /// response, alongside everything else the screen needs.
  final String? error;

  /// The platform whose row is showing its newer builds' release notes, or null for none. One at
  /// a time, like the Applications screen's instructions panel: the panel is spliced across the
  /// table's full width, and two open at once is a page of notes with the table lost between them.
  final String? expandedPlatform;

  @override
  List<Object?> get props => [view, loading, refreshing, error, expandedPlatform];
}

/// The Clients screen.
///
/// Does not poll: nothing here changes unless someone presses refresh, and the upstream check the
/// server performs on every read costs a GitHub API call. The old page checked once per page load
/// and this does the same — once per visit.
class ClientsBloc extends Bloc<ClientsEvent, ClientsState> {
  ClientsBloc({required GetClientsView getClientsView, required RefreshClients refreshClients})
      : _getClientsView = getClientsView,
        _refreshClients = refreshClients,
        super(const ClientsState()) {
    on<ClientsRequested>(_onRequested);
    on<ClientsRefreshRequested>(_onRefresh);
    on<ClientsRowExpansionToggled>((event, emit) => emit(ClientsState(
          view: state.view,
          loading: state.loading,
          refreshing: state.refreshing,
          error: state.error,
          expandedPlatform: state.expandedPlatform == event.platform ? null : event.platform,
        )));
  }

  final GetClientsView _getClientsView;
  final RefreshClients _refreshClients;

  Future<void> _onRequested(ClientsRequested event, Emitter<ClientsState> emit) async {
    emit(const ClientsState(loading: true));
    try {
      emit(ClientsState(view: await _getClientsView(), loading: false));
    } on ApiException catch (error) {
      emit(ClientsState(loading: false, error: error.message));
    }
  }

  Future<void> _onRefresh(ClientsRefreshRequested event, Emitter<ClientsState> emit) async {
    // The expanded row survives the refresh: what it lists shrinks to nothing once the import
    // lands, and "up to date" under a row that read "two builds behind" a moment ago is the
    // confirmation the person who pressed the button is looking for.
    emit(ClientsState(
      view: state.view,
      loading: false,
      refreshing: true,
      expandedPlatform: state.expandedPlatform,
    ));
    try {
      // The refresh returns the whole screen's state, so there is no follow-up read and no moment
      // where the import results sit beside the packages they just replaced.
      emit(ClientsState(
        view: await _refreshClients(),
        loading: false,
        expandedPlatform: state.expandedPlatform,
      ));
    } on ApiException catch (error) {
      emit(ClientsState(
        view: state.view,
        loading: false,
        error: error.message,
        expandedPlatform: state.expandedPlatform,
      ));
    }
  }
}
