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

final class ClientsState extends Equatable {
  const ClientsState({this.view, this.loading = true, this.refreshing = false, this.error});

  final ClientsView? view;
  final bool loading;
  final bool refreshing;

  /// A failure of the request itself. Distinct from `view.refreshError`, which is the server
  /// reporting that it could not list the upstream releases — that one arrives on a successful
  /// response, alongside everything else the screen needs.
  final String? error;

  @override
  List<Object?> get props => [view, loading, refreshing, error];
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
    emit(ClientsState(view: state.view, loading: false, refreshing: true));
    try {
      // The refresh returns the whole screen's state, so there is no follow-up read and no moment
      // where the import results sit beside the packages they just replaced.
      emit(ClientsState(view: await _refreshClients(), loading: false));
    } on ApiException catch (error) {
      emit(ClientsState(view: state.view, loading: false, error: error.message));
    }
  }
}
