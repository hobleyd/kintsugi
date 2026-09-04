import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../domain/entities/application.dart';
import '../../domain/entities/upgrade_path.dart';
import '../../domain/usecases/application_usecases.dart';

/// One row of the flattened table: an application-and-platform pairing, or an application with no
/// researched path at all.
///
/// Flattened here rather than in the widget because the filters and the sort operate on rows, not
/// on applications: an application installed on both macOS and Windows is two rows, and "update
/// available" can be true of one and false of the other.
class ApplicationTableRow extends Equatable {
  const ApplicationTableRow({
    required this.application,
    required this.upgradePath,
    required this.isChild,
  });

  final ApplicationRow application;

  /// Null for an application nothing has been researched for yet.
  final UpgradePathSummary? upgradePath;

  final bool isChild;

  String get statusKey => upgradePath?.statusKey ?? 'not-checked';

  String get platform => upgradePath?.platform ?? '';

  /// A stable identity for the row, used to key the expanded panel.
  String get key => '${application.name} $platform';

  @override
  List<Object?> get props => [application, upgradePath, isChild];
}

/// The filters the table applies, all client-side — everything they need is already in the
/// response, so there is no round trip.
class ApplicationFilters extends Equatable {
  const ApplicationFilters({this.search = '', this.statusKey = 'all', this.hostName = 'all'});

  final String search;
  final String statusKey;
  final String hostName;

  bool get isActive => search.isNotEmpty || statusKey != 'all' || hostName != 'all';

  ApplicationFilters copyWith({String? search, String? statusKey, String? hostName}) =>
      ApplicationFilters(
        search: search ?? this.search,
        statusKey: statusKey ?? this.statusKey,
        hostName: hostName ?? this.hostName,
      );

  /// Whether one row survives these filters.
  bool matches(ApplicationTableRow row) {
    if (search.isNotEmpty && !row.application.name.toLowerCase().contains(search.toLowerCase())) {
      return false;
    }
    if (statusKey != 'all' && row.statusKey != statusKey) return false;
    if (hostName == 'all') return true;

    final host = hostName.toLowerCase();

    // "Update Available" is fleet-wide -- true if *any* host is behind -- so pairing it with a host
    // filter and testing only "is it installed here" would surface applications this host is
    // already current on, just because some other host is not. When both filters are active,
    // narrow to hosts specifically behind on this application.
    if (statusKey == 'update-available') {
      final outdated = row.upgradePath?.hostNamesNeedingUpdate ?? const <String>[];
      return outdated.any((h) => h.toLowerCase() == host);
    }

    // The row's *own* hosts, not the application's. `ApplicationRow.hostNames` is keyed on the
    // application's name alone, so an application installed from Homebrew on a Mac and from winget
    // on a PC is two rows sharing one host list -- and testing that list left the `pm:Homebrew` row
    // on screen, labelled as such, under a filter naming a Windows host.
    //
    // Falling back to it when the path has no host list of its own covers two cases and is the safe
    // direction in both: a row with no researched path at all has no per-row list to consult, and
    // an empty one from a server that predates the field would otherwise empty the table under any
    // host filter (the bundle nginx serves and the API are separate images, either of which can be
    // rebuilt without the other).
    final rowHostNames = row.upgradePath?.hostNames ?? const <String>[];
    final hostNames = rowHostNames.isEmpty ? row.application.hostNames : rowHostNames;

    return hostNames.any((h) => h.toLowerCase() == host);
  }

  @override
  List<Object?> get props => [search, statusKey, hostName];
}

/// How the table is ordered.
class ApplicationSort extends Equatable {
  const ApplicationSort(this.key, {this.ascending = true});

  final String key;
  final bool ascending;

  ApplicationSort toggled() => ApplicationSort(key, ascending: !ascending);

  @override
  List<Object?> get props => [key, ascending];
}

sealed class ApplicationsEvent extends Equatable {
  const ApplicationsEvent();

  @override
  List<Object?> get props => const [];
}

final class ApplicationsRequested extends ApplicationsEvent {
  const ApplicationsRequested({this.showSpinner = true});

  /// False for a background poll, so a refresh does not blank a table that is already on screen.
  final bool showSpinner;

  @override
  List<Object?> get props => [showSpinner];
}

final class ApplicationsFiltersChanged extends ApplicationsEvent {
  const ApplicationsFiltersChanged(this.filters);

  final ApplicationFilters filters;

  @override
  List<Object?> get props => [filters];
}

final class ApplicationsSortChanged extends ApplicationsEvent {
  const ApplicationsSortChanged(this.key);

  final String key;

  @override
  List<Object?> get props => [key];
}

final class ApplicationRowExpansionToggled extends ApplicationsEvent {
  const ApplicationRowExpansionToggled(this.rowKey);

  final String rowKey;

  @override
  List<Object?> get props => [rowKey];
}

final class ApplicationsState extends Equatable {
  const ApplicationsState({
    this.overview = const ApplicationOverview.empty(),
    this.filters = const ApplicationFilters(),
    this.sort,
    this.expandedRowKey,
    this.loading = true,
    this.error,
  });

  final ApplicationOverview overview;
  final ApplicationFilters filters;
  final ApplicationSort? sort;

  /// Only one panel is open at a time. The page this replaces allowed several and then had to
  /// close them all whenever the table reordered, because a panel spliced under a row that has
  /// moved is worse than no panel; one at a time makes that impossible rather than handled.
  final String? expandedRowKey;

  final bool loading;
  final String? error;

  /// Every row the response produced, before filtering, with children flattened in directly after
  /// their parent so the nesting survives a sort.
  List<ApplicationTableRow> get allRows {
    final rows = <ApplicationTableRow>[];
    for (final application in overview.applications) {
      rows.addAll(_rowsFor(application, isChild: false));
      for (final child in application.children) {
        rows.addAll(_rowsFor(child, isChild: true));
      }
    }
    return rows;
  }

  /// The rows on screen: filtered, then sorted.
  List<ApplicationTableRow> get visibleRows {
    final rows = allRows.where(filters.matches).toList();
    final order = sort;
    if (order == null) return rows;

    rows.sort((a, b) {
      final comparison = switch (order.key) {
        'name' => a.application.name.toLowerCase().compareTo(b.application.name.toLowerCase()),
        'hosts' => a.application.hostCount.compareTo(b.application.hostCount),
        'platform' => a.platform.toLowerCase().compareTo(b.platform.toLowerCase()),
        'status' => a.statusKey.compareTo(b.statusKey),
        'latest' =>
          (a.upgradePath?.latestVersion ?? '').compareTo(b.upgradePath?.latestVersion ?? ''),
        'checked' => (a.upgradePath?.checkedUtc ?? DateTime(0))
            .compareTo(b.upgradePath?.checkedUtc ?? DateTime(0)),
        _ => 0,
      };
      return order.ascending ? comparison : -comparison;
    });
    return rows;
  }

  static List<ApplicationTableRow> _rowsFor(ApplicationRow application, {required bool isChild}) {
    if (application.upgradePaths.isEmpty) {
      return [ApplicationTableRow(application: application, upgradePath: null, isChild: isChild)];
    }
    return [
      for (final path in application.upgradePaths)
        ApplicationTableRow(application: application, upgradePath: path, isChild: isChild),
    ];
  }

  ApplicationsState copyWith({
    ApplicationOverview? overview,
    ApplicationFilters? filters,
    ApplicationSort? sort,
    String? expandedRowKey,
    bool? loading,
    String? error,
    bool clearError = false,
    bool clearExpanded = false,
  }) =>
      ApplicationsState(
        overview: overview ?? this.overview,
        filters: filters ?? this.filters,
        sort: sort ?? this.sort,
        expandedRowKey: clearExpanded ? null : (expandedRowKey ?? this.expandedRowKey),
        loading: loading ?? this.loading,
        error: clearError ? null : (error ?? this.error),
      );

  @override
  List<Object?> get props => [overview, filters, sort, expandedRowKey, loading, error];
}

class ApplicationsBloc extends Bloc<ApplicationsEvent, ApplicationsState>
    with Polling<ApplicationsEvent, ApplicationsState> {
  ApplicationsBloc({
    required GetApplicationOverview getOverview,
    ApplicationFilters initialFilters = const ApplicationFilters(),
  })  : _getOverview = getOverview,
        super(ApplicationsState(filters: initialFilters)) {
    on<ApplicationsRequested>(_onRequested);
    on<ApplicationsFiltersChanged>((event, emit) => emit(state.copyWith(
          filters: event.filters,
          // A panel spliced under a row that a filter change may have hidden is stranded, so it
          // closes with the change rather than being reconciled afterwards.
          clearExpanded: true,
        )));
    on<ApplicationsSortChanged>((event, emit) => emit(state.copyWith(
          sort: state.sort?.key == event.key ? state.sort!.toggled() : ApplicationSort(event.key),
          clearExpanded: true,
        )));
    on<ApplicationRowExpansionToggled>((event, emit) => emit(
          state.expandedRowKey == event.rowKey
              ? state.copyWith(clearExpanded: true)
              : state.copyWith(expandedRowKey: event.rowKey),
        ));

    // Slower than the three seconds the background runs poll at: this is a large response, and a
    // resolved upgrade path only changes when one of those runs or a human changes it. It is here
    // so an agent's inventory report showing up is visible without a reload.
    startPolling(const Duration(seconds: 60), const ApplicationsRequested(showSpinner: false));
  }

  final GetApplicationOverview _getOverview;

  static ApplicationFilters _normalizeHostFilter(
    ApplicationFilters filters,
    List<String> hostNames,
  ) {
    if (filters.hostName == 'all' || hostNames.contains(filters.hostName)) return filters;

    for (final hostName in hostNames) {
      if (hostName.toLowerCase() == filters.hostName.toLowerCase()) {
        return filters.copyWith(hostName: hostName);
      }
    }

    // Named a host this fleet has never reported. Dropping the filter rather than keeping it is
    // the honest outcome: keeping it would show an empty table with a control that offers no way
    // to say what is being filtered out.
    return filters.copyWith(hostName: 'all');
  }

  Future<void> _onRequested(ApplicationsRequested event, Emitter<ApplicationsState> emit) async {
    if (event.showSpinner) emit(state.copyWith(loading: true, clearError: true));

    try {
      final overview = await _getOverview();
      emit(state.copyWith(
        overview: overview,
        // A host filter arriving from a deep link carries whatever casing the linking screen had,
        // and the dropdown matches its options by value. Normalising to the casing the response
        // actually uses is what keeps the control showing the host it is filtering by, instead of
        // filtering correctly while looking unset.
        filters: _normalizeHostFilter(state.filters, overview.allHostNames),
        loading: false,
        clearError: true,
      ));
    } on ApiException catch (error) {
      // A failed poll leaves the table alone and says so, rather than replacing working data with
      // an error because one background refresh missed.
      emit(state.copyWith(loading: false, error: error.message));
    }
  }
}
