import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../domain/entities/host.dart';
import '../../domain/usecases/host_usecases.dart';

sealed class HostsEvent extends Equatable {
  const HostsEvent();

  @override
  List<Object?> get props => const [];
}

final class HostsRequested extends HostsEvent {
  const HostsRequested({this.showSpinner = true});

  /// False for a background poll, so a refresh does not blank the table that is already on screen.
  final bool showSpinner;

  @override
  List<Object?> get props => [showSpinner];
}

final class HostRemovalRequested extends HostsEvent {
  const HostRemovalRequested(this.host);

  final HostSummary host;

  @override
  List<Object?> get props => [host];
}

final class HostsMessageDismissed extends HostsEvent {
  const HostsMessageDismissed();
}

final class HostsState extends Equatable {
  const HostsState({
    this.hosts = const [],
    this.loading = true,
    this.error,
    this.notice,
    this.removingId,
  });

  final List<HostSummary> hosts;
  final bool loading;
  final String? error;
  final String? notice;

  /// The host whose removal is in flight, so its button can be disabled without disabling every
  /// other row's.
  final String? removingId;

  HostsState copyWith({
    List<HostSummary>? hosts,
    bool? loading,
    String? error,
    String? notice,
    String? removingId,
    bool clearError = false,
    bool clearNotice = false,
    bool clearRemoving = false,
  }) =>
      HostsState(
        hosts: hosts ?? this.hosts,
        loading: loading ?? this.loading,
        error: clearError ? null : (error ?? this.error),
        notice: clearNotice ? null : (notice ?? this.notice),
        removingId: clearRemoving ? null : (removingId ?? this.removingId),
      );

  @override
  List<Object?> get props => [hosts, loading, error, notice, removingId];
}

/// The Hosts screen.
///
/// Polls, which the page it replaces did not: a host's status and last-seen move on their own as
/// agents check in, and watching them arrive is the point of the screen. Thirty seconds rather
/// than the three the background-run screens use — nothing here changes faster than a check-in
/// interval, and the states are value-compared, so a poll that finds nothing new emits an
/// identical state and rebuilds nothing.
class HostsBloc extends Bloc<HostsEvent, HostsState> with Polling<HostsEvent, HostsState> {
  HostsBloc({required GetHosts getHosts, required RequestHostRemoval requestHostRemoval})
      : _getHosts = getHosts,
        _requestHostRemoval = requestHostRemoval,
        super(const HostsState()) {
    on<HostsRequested>(_onRequested);
    on<HostRemovalRequested>(_onRemovalRequested);
    on<HostsMessageDismissed>(
      (_, emit) => emit(state.copyWith(clearError: true, clearNotice: true)),
    );

    startPolling(const Duration(seconds: 30), const HostsRequested(showSpinner: false));
  }

  final GetHosts _getHosts;
  final RequestHostRemoval _requestHostRemoval;

  Future<void> _onRequested(HostsRequested event, Emitter<HostsState> emit) async {
    if (event.showSpinner) emit(state.copyWith(loading: true, clearError: true));

    try {
      emit(state.copyWith(hosts: await _getHosts(), loading: false, clearError: true));
    } on ApiException catch (error) {
      // A failed poll leaves the table alone and says so. Replacing a working list with an error
      // because one background refresh missed would be worse than showing slightly stale data.
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onRemovalRequested(HostRemovalRequested event, Emitter<HostsState> emit) async {
    emit(state.copyWith(removingId: event.host.id, clearError: true, clearNotice: true));

    try {
      await _requestHostRemoval(event.host.id);
      emit(state.copyWith(
        clearRemoving: true,
        notice: '${event.host.hostname} will uninstall its agent on its next check-in.',
      ));
      add(const HostsRequested(showSpinner: false));
    } on ApiException catch (error) {
      emit(state.copyWith(
        clearRemoving: true,
        error: 'Could not remove ${event.host.hostname}: ${error.message}',
      ));
    }
  }
}
