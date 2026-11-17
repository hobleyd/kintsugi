import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../domain/entities/enums.dart';
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

final class HostsSearchChanged extends HostsEvent {
  const HostsSearchChanged(this.search);

  final String search;

  @override
  List<Object?> get props => [search];
}

final class HostsAgentFilterChanged extends HostsEvent {
  const HostsAgentFilterChanged(this.filter);

  /// One of [hostsAgentFilterOptions]'s keys.
  final String filter;

  @override
  List<Object?> get props => [filter];
}

/// The Agent filter's choices: every host, those behind the newest agent for their platform, and
/// those the server thinks are stuck there.
const hostsAgentFilterOptions = <String, String>{
  'all': 'All hosts',
  'behind': 'Behind the latest',
  'stuck': 'Probably stuck',
};

final class HostsState extends Equatable {
  const HostsState({
    this.hosts = const [],
    this.search = '',
    this.agentFilter = 'all',
    this.loading = true,
    this.error,
    this.notice,
    this.removingId,
  });

  /// Every host the server returned. The table shows [visibleHosts]; the subtitle counts these.
  final List<HostSummary> hosts;

  /// The text in the Hostname column's search box. Applied client-side — everything it needs is
  /// already in the response, so there is no round trip, and a poll that arrives mid-typing
  /// replaces [hosts] without touching this.
  final String search;

  /// One of [hostsAgentFilterOptions]'s keys.
  final String agentFilter;

  final bool loading;
  final String? error;
  final String? notice;

  /// The host whose removal is in flight, so its button can be disabled without disabling every
  /// other row's.
  final String? removingId;

  /// The hosts the search leaves on screen. Matches the three columns an administrator identifies a
  /// host by — its name, its serial number and its address — case-insensitively, as a substring, so
  /// a fragment of a hostname or the tail of a serial is enough to find one machine in a fleet.
  List<HostSummary> get visibleHosts {
    final byAgent = [for (final host in hosts) if (_matchesAgent(host)) host];
    if (search.isEmpty) return byAgent;
    final needle = search.toLowerCase();
    return [
      for (final host in byAgent)
        if (host.hostname.toLowerCase().contains(needle) ||
            host.serialNumber.toLowerCase().contains(needle) ||
            (host.ipAddress?.toLowerCase().contains(needle) ?? false))
          host,
    ];
  }

  bool _matchesAgent(HostSummary host) => switch (agentFilter) {
        'behind' => host.agentUpdateState.isBehind,
        'stuck' => host.agentUpdateState == AgentUpdateState.notUpdating,
        _ => true,
      };

  /// Hosts behind the newest agent for their platform, and of those the ones that look stuck — the
  /// banner above the table. Counted over every host, not the filtered ones, so choosing a filter
  /// never changes the number it was chosen from.
  int get behindCount => hosts.where((h) => h.agentUpdateState.isBehind).length;
  int get stuckCount => hosts.where((h) => h.agentUpdateState == AgentUpdateState.notUpdating).length;

  HostsState copyWith({
    List<HostSummary>? hosts,
    String? search,
    String? agentFilter,
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
        search: search ?? this.search,
        agentFilter: agentFilter ?? this.agentFilter,
        loading: loading ?? this.loading,
        error: clearError ? null : (error ?? this.error),
        notice: clearNotice ? null : (notice ?? this.notice),
        removingId: clearRemoving ? null : (removingId ?? this.removingId),
      );

  @override
  List<Object?> get props => [hosts, search, agentFilter, loading, error, notice, removingId];
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
    on<HostsSearchChanged>((event, emit) => emit(state.copyWith(search: event.search)));
    on<HostsAgentFilterChanged>((event, emit) => emit(state.copyWith(agentFilter: event.filter)));

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
