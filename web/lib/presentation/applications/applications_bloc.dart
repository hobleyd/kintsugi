import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../domain/entities/application.dart';
import '../../domain/entities/upgrade_path.dart';
import '../../domain/usecases/application_usecases.dart';
import '../../domain/usecases/upgrade_path_usecases.dart';

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
    this.parentName,
    this.matchingChildCount = 0,
  });

  final ApplicationRow application;

  /// Null for an application nothing has been researched for yet.
  final UpgradePathSummary? upgradePath;

  /// The package manager this application is listed under, or null for a top-level row. It is
  /// the name the parent's rows are expanded and collapsed by — see
  /// [ApplicationsState.expandedManagerNames].
  final String? parentName;

  /// How many of this application's children survive the current filters. Only a top-level row
  /// with children has a non-zero count, and it is what decides whether the row gets an expander:
  /// a manager whose every application is filtered out has nothing to expand, and a control that
  /// toggles nothing visible would read as broken.
  final int matchingChildCount;

  bool get isChild => parentName != null;

  String get statusKey => upgradePath?.statusKey ?? 'not-checked';

  String get platform => upgradePath?.platform ?? '';

  /// Whether this row's path is the package manager's own shared script — a child's `pm:` row.
  /// Every application a manager owns carries byte-identical script content (see
  /// `PackageManagerCatalog` on the server), so it is shown once, on the manager's own row, and
  /// each child shows only what is its own: its versions and its version check. A child can still
  /// have an OS-bucket row of its own — the same name installed standalone on another host — and
  /// that row's script is that application's, so it stays.
  bool get usesManagerScript => isChild && platform.startsWith('pm:');

  /// Whether the Actions column offers the AI-instructions panel for this row. A row nested under
  /// a manager holds either the manager's shared script — researched by nobody, and signed from
  /// the manager's row — or no row at all yet; AI instructions apply to neither. The manager's own
  /// row keeps the panel, since that is where its script is read and signed, and so does a child's
  /// OS-bucket row, whose script is its own.
  bool get offersInstructions => !isChild || (upgradePath != null && !usesManagerScript);

  /// A stable identity for the row, used to key the expanded panel.
  String get key => '${application.name} $platform';

  @override
  List<Object?> get props => [application, upgradePath, parentName, matchingChildCount];
}

/// The filters the table applies, all client-side — everything they need is already in the
/// response, so there is no round trip.
class ApplicationFilters extends Equatable {
  const ApplicationFilters({
    this.search = '',
    this.statusKey = 'all',
    this.hostName = 'all',
    this.platform = 'all',
  });

  final String search;
  final String statusKey;
  final String hostName;

  /// A platform bucket as the server names it (`macOS`, `pm:Homebrew`, ...), or `all`. Matched
  /// exactly rather than case-insensitively: the options are read off the response itself
  /// ([ApplicationsState.platformOptions]), so there is no other spelling to reconcile.
  final String platform;

  bool get isActive =>
      search.isNotEmpty || statusKey != 'all' || hostName != 'all' || platform != 'all';

  ApplicationFilters copyWith({
    String? search,
    String? statusKey,
    String? hostName,
    String? platform,
  }) =>
      ApplicationFilters(
        search: search ?? this.search,
        statusKey: statusKey ?? this.statusKey,
        hostName: hostName ?? this.hostName,
        platform: platform ?? this.platform,
      );

  /// Whether one row survives these filters.
  bool matches(ApplicationTableRow row) {
    if (search.isNotEmpty && !row.application.name.toLowerCase().contains(search.toLowerCase())) {
      return false;
    }
    if (statusKey != 'all' && row.statusKey != statusKey) return false;
    // A row with no researched path has no platform, so it is excluded under any specific one --
    // the same answer the host filter gives for a host the row is not on.
    if (platform != 'all' && row.platform != platform) return false;
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
  List<Object?> get props => [search, statusKey, hostName, platform];
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

/// Shows or hides the applications listed under one package manager, by the manager's name.
final class ApplicationChildrenToggled extends ApplicationsEvent {
  const ApplicationChildrenToggled(this.applicationName);

  final String applicationName;

  @override
  List<Object?> get props => [applicationName];
}

/// Re-runs one row's script to see whether a newer version has been released — the per-row form of
/// the "Check for Updates" button, and like it, no AI call.
final class ApplicationUpdateCheckRequested extends ApplicationsEvent {
  const ApplicationUpdateCheckRequested(this.row);

  final ApplicationTableRow row;

  @override
  List<Object?> get props => [row];
}

/// What the most recent per-row version check reported, shown above the table.
///
/// Shown there rather than in the row because the row's own columns cannot say "unchanged": a
/// check that succeeded and found nothing new leaves Latest exactly as it was, which without this
/// looks identical to the icon having done nothing.
class UpdateCheckNotice extends Equatable {
  const UpdateCheckNotice({required this.message, required this.success});

  final String message;
  final bool success;

  @override
  List<Object?> get props => [message, success];
}

final class ApplicationsState extends Equatable {
  const ApplicationsState({
    this.overview = const ApplicationOverview.empty(),
    this.filters = const ApplicationFilters(),
    this.sort,
    this.expandedRowKey,
    this.expandedManagerNames = const {},
    this.loading = true,
    this.error,
    this.checkingRowKeys = const {},
    this.checkNotice,
  });

  final ApplicationOverview overview;
  final ApplicationFilters filters;
  final ApplicationSort? sort;

  /// Only one panel is open at a time. The page this replaces allowed several and then had to
  /// close them all whenever the table reordered, because a panel spliced under a row that has
  /// moved is worse than no panel; one at a time makes that impossible rather than handled.
  final String? expandedRowKey;

  /// Package managers whose applications are shown under them, by the manager's name. Collapsed
  /// by default: a manager's applications all share one script and one status mechanism, so the
  /// manager's row says most of what the table has to say about them, and a Homebrew fleet lists
  /// dozens of formulae that would otherwise bury the applications researched individually.
  /// Keyed by name rather than [ApplicationTableRow.key] because the children belong to the
  /// application, not to one of its platform rows.
  final Set<String> expandedManagerNames;

  final bool loading;
  final String? error;

  /// Rows whose version check is in flight, by [ApplicationTableRow.key]. A set rather than one
  /// key because each check is a synchronous round trip of up to 30 seconds and nothing stops a
  /// reader pressing a second row's icon while the first is still running.
  final Set<String> checkingRowKeys;

  final UpdateCheckNotice? checkNotice;

  /// Every row the response produced, before filtering, with children flattened in directly after
  /// their parent.
  List<ApplicationTableRow> get allRows => [
        for (final application in overview.applications) ...[
          ..._rowsFor(application),
          for (final child in application.children)
            ..._rowsFor(child, parentName: application.name),
        ],
      ];

  /// Every platform bucket the response names, sorted, for the Platform column's filter. Read off
  /// the rows rather than listed statically: the buckets a fleet actually has depend on which
  /// agents and package managers report, and a fixed list would offer platforms with nothing
  /// under them. Rows with no researched path have no platform and contribute nothing.
  List<String> get platformOptions {
    final platforms = <String>{
      for (final row in allRows)
        if (row.platform.isNotEmpty) row.platform,
    };
    return platforms.toList()
      ..sort((a, b) => a.toLowerCase().compareTo(b.toLowerCase()));
  }

  /// The rows on screen: filtered, then sorted, with a package manager's applications kept
  /// directly under it.
  ///
  /// Sorted as groups rather than as one flat list — parents among parents, children among their
  /// siblings — because a flat sort by Latest or Checked scatters a manager's applications through
  /// the table, where an indented row with no manager above it says nothing about what it is
  /// indented under. Children are shown only while their manager is expanded, with one exception:
  /// a manager filtered out while some of its applications match — a search for one formula's
  /// name, or a status the manager's own row does not have — still shows those applications,
  /// because a filter that matched rows and then hid them all under a collapsed parent that is
  /// not on screen would leave nothing for the expander to be pressed on.
  List<ApplicationTableRow> get visibleRows {
    final groups = <({List<ApplicationTableRow> parents, List<ApplicationTableRow> children})>[];
    for (final application in overview.applications) {
      final children = [
        for (final child in application.children)
          ..._rowsFor(child, parentName: application.name).where(filters.matches),
      ];
      final parents = _rowsFor(application, matchingChildCount: children.length)
          .where(filters.matches)
          .toList();
      if (parents.isNotEmpty || children.isNotEmpty) {
        groups.add((parents: parents, children: children));
      }
    }

    final order = sort;
    if (order != null) {
      int compare(ApplicationTableRow a, ApplicationTableRow b) {
        final comparison = switch (order.key) {
          'name' =>
            a.application.name.toLowerCase().compareTo(b.application.name.toLowerCase()),
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
      }

      for (final group in groups) {
        group.parents.sort(compare);
        group.children.sort(compare);
      }
      // A group takes its place from its first row — the manager's own when it is on screen, else
      // the first of the orphaned children.
      groups.sort((a, b) => compare(
            a.parents.isNotEmpty ? a.parents.first : a.children.first,
            b.parents.isNotEmpty ? b.parents.first : b.children.first,
          ));
    }

    return [
      for (final group in groups) ...[
        ...group.parents,
        if (group.parents.isEmpty ||
            expandedManagerNames.contains(group.parents.first.application.name))
          ...group.children,
      ],
    ];
  }

  static List<ApplicationTableRow> _rowsFor(
    ApplicationRow application, {
    String? parentName,
    int matchingChildCount = 0,
  }) {
    if (application.upgradePaths.isEmpty) {
      return [
        ApplicationTableRow(
          application: application,
          upgradePath: null,
          parentName: parentName,
          matchingChildCount: matchingChildCount,
        ),
      ];
    }
    return [
      for (final path in application.upgradePaths)
        ApplicationTableRow(
          application: application,
          upgradePath: path,
          parentName: parentName,
          matchingChildCount: matchingChildCount,
        ),
    ];
  }

  ApplicationsState copyWith({
    ApplicationOverview? overview,
    ApplicationFilters? filters,
    ApplicationSort? sort,
    String? expandedRowKey,
    Set<String>? expandedManagerNames,
    bool? loading,
    String? error,
    Set<String>? checkingRowKeys,
    UpdateCheckNotice? checkNotice,
    bool clearError = false,
    bool clearExpanded = false,
    bool clearCheckNotice = false,
  }) =>
      ApplicationsState(
        overview: overview ?? this.overview,
        filters: filters ?? this.filters,
        sort: sort ?? this.sort,
        expandedRowKey: clearExpanded ? null : (expandedRowKey ?? this.expandedRowKey),
        expandedManagerNames: expandedManagerNames ?? this.expandedManagerNames,
        loading: loading ?? this.loading,
        error: clearError ? null : (error ?? this.error),
        checkingRowKeys: checkingRowKeys ?? this.checkingRowKeys,
        checkNotice: clearCheckNotice ? null : (checkNotice ?? this.checkNotice),
      );

  @override
  List<Object?> get props => [
        overview,
        filters,
        sort,
        expandedRowKey,
        expandedManagerNames,
        loading,
        error,
        checkingRowKeys,
        checkNotice,
      ];
}

class ApplicationsBloc extends Bloc<ApplicationsEvent, ApplicationsState>
    with Polling<ApplicationsEvent, ApplicationsState> {
  ApplicationsBloc({
    required GetApplicationOverview getOverview,
    required CheckApplicationUpdate checkUpdate,
    ApplicationFilters initialFilters = const ApplicationFilters(),
  })  : _getOverview = getOverview,
        _checkUpdate = checkUpdate,
        super(ApplicationsState(filters: initialFilters)) {
    on<ApplicationsRequested>(_onRequested);
    on<ApplicationUpdateCheckRequested>(_onUpdateCheckRequested);
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
    // The instructions panel is left alone: a child's panel simply leaves the table with the row
    // and comes back with it, since the table only builds panels for rows it is showing.
    on<ApplicationChildrenToggled>((event, emit) => emit(state.copyWith(
          expandedManagerNames: state.expandedManagerNames.contains(event.applicationName)
              ? ({...state.expandedManagerNames}..remove(event.applicationName))
              : {...state.expandedManagerNames, event.applicationName},
        )));

    // Slower than the three seconds the background runs poll at: this is a large response, and a
    // resolved upgrade path only changes when one of those runs or a human changes it. It is here
    // so an agent's inventory report showing up is visible without a reload.
    startPolling(const Duration(seconds: 60), const ApplicationsRequested(showSpinner: false));
  }

  final GetApplicationOverview _getOverview;
  final CheckApplicationUpdate _checkUpdate;

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

  Future<void> _onUpdateCheckRequested(
    ApplicationUpdateCheckRequested event,
    Emitter<ApplicationsState> emit,
  ) async {
    final row = event.row;
    if (state.checkingRowKeys.contains(row.key)) return;

    emit(state.copyWith(
      checkingRowKeys: {...state.checkingRowKeys, row.key},
      clearCheckNotice: true,
    ));

    final label = '${row.application.name} on ${row.platform}';
    UpdateCheckNotice notice;
    try {
      final result = await _checkUpdate(
        applicationName: row.application.name,
        platform: row.platform,
      );
      notice = UpdateCheckNotice(
        success: result.success,
        message: switch (result) {
          UpdateCheckResult(success: true, versionChanged: true) =>
            '$label: a newer version was found.',
          UpdateCheckResult(success: true) =>
            '$label: no newer version; the latest known version is unchanged.',
          UpdateCheckResult(note: final note?) => '$label: $note',
          _ => '$label: the version check failed.',
        },
      );
    } on ApiException catch (error) {
      notice = UpdateCheckNotice(message: '$label: ${error.message}', success: false);
    }

    // Read `state` afresh: a poll or another row's check may have emitted meanwhile, and this
    // handler runs concurrently with both.
    emit(state.copyWith(
      checkingRowKeys: {...state.checkingRowKeys}..remove(row.key),
      checkNotice: notice,
    ));

    // The result carries no version. The row's Latest and Checked columns come from the overview,
    // so it is re-read now rather than left to the next 60-second poll.
    add(const ApplicationsRequested(showSpinner: false));
  }
}
