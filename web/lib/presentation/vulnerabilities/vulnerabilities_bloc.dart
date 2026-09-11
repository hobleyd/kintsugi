import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/vulnerability.dart';
import '../../domain/usecases/vulnerability_usecases.dart';

sealed class VulnerabilitiesEvent extends Equatable {
  const VulnerabilitiesEvent();

  @override
  List<Object?> get props => const [];
}

final class VulnerabilitiesRequested extends VulnerabilitiesEvent {
  const VulnerabilitiesRequested();
}

/// Switches between the exploited set and everything matched. Its own event rather than a
/// parameter on the reload, because it is a different question of the server: the exploited set
/// is a handful of rows and the full set is tens of thousands, so the filter is applied there.
final class VulnerabilitiesFilterChanged extends VulnerabilitiesEvent {
  const VulnerabilitiesFilterChanged(this.knownExploitedOnly);

  final bool knownExploitedOnly;

  @override
  List<Object?> get props => [knownExploitedOnly];
}

class VulnerabilitiesState extends Equatable {
  const VulnerabilitiesState({
    this.overview,
    this.loading = true,
    this.knownExploitedOnly = true,
    this.error,
  });

  final VulnerabilityOverview? overview;
  final bool loading;
  final bool knownExploitedOnly;
  final String? error;

  VulnerabilitiesState copyWith({
    VulnerabilityOverview? overview,
    bool? loading,
    bool? knownExploitedOnly,
    String? error,
    bool clearError = false,
  }) =>
      VulnerabilitiesState(
        overview: overview ?? this.overview,
        loading: loading ?? this.loading,
        knownExploitedOnly: knownExploitedOnly ?? this.knownExploitedOnly,
        error: clearError ? null : (error ?? this.error),
      );

  @override
  List<Object?> get props => [overview, loading, knownExploitedOnly, error];
}

class VulnerabilitiesBloc extends Bloc<VulnerabilitiesEvent, VulnerabilitiesState> {
  VulnerabilitiesBloc({required GetVulnerabilityOverview getOverview})
      : _getOverview = getOverview,
        super(const VulnerabilitiesState()) {
    on<VulnerabilitiesRequested>((_, emit) => _load(emit, state.knownExploitedOnly));
    on<VulnerabilitiesFilterChanged>((event, emit) => _load(emit, event.knownExploitedOnly));
  }

  final GetVulnerabilityOverview _getOverview;

  Future<void> _load(Emitter<VulnerabilitiesState> emit, bool knownExploitedOnly) async {
    emit(state.copyWith(loading: true, knownExploitedOnly: knownExploitedOnly, clearError: true));
    try {
      emit(state.copyWith(
        overview: await _getOverview(knownExploitedOnly: knownExploitedOnly),
        loading: false,
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }
}

// ---------------------------------------------------------------------------------------------
// The CPE mapping queue, which is its own bloc on the same screen.
// ---------------------------------------------------------------------------------------------

sealed class CpeMappingsEvent extends Equatable {
  const CpeMappingsEvent();

  @override
  List<Object?> get props => const [];
}

final class CpeMappingsRequested extends CpeMappingsEvent {
  const CpeMappingsRequested();
}

final class CpeDictionarySearched extends CpeMappingsEvent {
  const CpeDictionarySearched({required this.mappingId, required this.keyword});

  final String mappingId;
  final String keyword;

  @override
  List<Object?> get props => [mappingId, keyword];
}

final class CpeMappingConfirmed extends CpeMappingsEvent {
  const CpeMappingConfirmed({required this.id, required this.vendor, required this.product});

  final String id;
  final String vendor;
  final String product;

  @override
  List<Object?> get props => [id, vendor, product];
}

final class CpeMappingDismissed extends CpeMappingsEvent {
  const CpeMappingDismissed(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

final class CpeMappingReset extends CpeMappingsEvent {
  const CpeMappingReset(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

final class CpeMappingFiltersChanged extends CpeMappingsEvent {
  const CpeMappingFiltersChanged(this.filters);

  final CpeMappingFilters filters;

  @override
  List<Object?> get props => [filters];
}

final class CpeMappingSelectionToggled extends CpeMappingsEvent {
  const CpeMappingSelectionToggled(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

/// Ticks or clears every row the current filters leave visible — never the ones they hide, which
/// is the only reading of a header checkbox that cannot confirm something the reviewer cannot see.
final class CpeMappingVisibleSelectionToggled extends CpeMappingsEvent {
  const CpeMappingVisibleSelectionToggled(this.selected);

  final bool selected;

  @override
  List<Object?> get props => [selected];
}

final class CpeMappingSelectionCleared extends CpeMappingsEvent {
  const CpeMappingSelectionCleared();
}

/// Opens one row's detail — the dictionary search, the installed versions and any error — and
/// closes whatever was open, the way the Applications screen's instructions panel behaves.
final class CpeMappingExpansionToggled extends CpeMappingsEvent {
  const CpeMappingExpansionToggled(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

final class CpeMappingsBulkConfirmed extends CpeMappingsEvent {
  const CpeMappingsBulkConfirmed();
}

final class CpeMappingsBulkReset extends CpeMappingsEvent {
  const CpeMappingsBulkReset();
}

final class CpeMappingsBulkResultDismissed extends CpeMappingsEvent {
  const CpeMappingsBulkResultDismissed();
}

/// What the queue's header row is filtering on. Every column carries one, because the queue is the
/// screen an administrator drains a backlog from: "show me the low-confidence Windows rows nothing
/// has assessed" is the question, and eight columns of unfiltered rows is not an answer.
class CpeMappingFilters extends Equatable {
  const CpeMappingFilters({
    this.application = '',
    this.cpe = '',
    this.vendor = '',
    this.product = '',
    this.confidence = 'all',
    this.status = 'all',
    this.hosts = '',
    this.cves = 'all',
  });

  final String application;
  final String cpe;
  final String vendor;
  final String product;

  /// `all`, or a [CpeConfidence] name.
  final String confidence;

  /// `all`, or a [CpeMappingStatus] name.
  final String status;

  /// A minimum host count, as typed. Non-numeric text matches everything rather than nothing: a
  /// half-typed filter must not blank the table.
  final String hosts;

  /// `all`, `with`, `exploited` or `none`.
  final String cves;

  CpeMappingFilters copyWith({
    String? application,
    String? cpe,
    String? vendor,
    String? product,
    String? confidence,
    String? status,
    String? hosts,
    String? cves,
  }) =>
      CpeMappingFilters(
        application: application ?? this.application,
        cpe: cpe ?? this.cpe,
        vendor: vendor ?? this.vendor,
        product: product ?? this.product,
        confidence: confidence ?? this.confidence,
        status: status ?? this.status,
        hosts: hosts ?? this.hosts,
        cves: cves ?? this.cves,
      );

  bool get isEmpty =>
      application.isEmpty &&
      cpe.isEmpty &&
      vendor.isEmpty &&
      product.isEmpty &&
      confidence == 'all' &&
      status == 'all' &&
      hosts.isEmpty &&
      cves == 'all';

  bool matches(CpeMapping mapping) {
    bool has(String needle, String? haystack) =>
        needle.isEmpty || (haystack ?? '').toLowerCase().contains(needle.toLowerCase());

    if (!has(application, mapping.displayName)) return false;
    if (!has(cpe, mapping.cpeName)) return false;
    if (!has(vendor, mapping.vendor)) return false;
    if (!has(product, mapping.product)) return false;
    if (confidence != 'all' && mapping.confidence.name != confidence) return false;
    if (status != 'all' && mapping.status.name != status) return false;

    final minimum = int.tryParse(hosts.trim());
    if (minimum != null && mapping.hostCount < minimum) return false;

    return switch (cves) {
      'with' => mapping.matchCount > 0,
      'exploited' => mapping.knownExploitedCount > 0,
      'none' => mapping.matchCount == 0,
      _ => true,
    };
  }

  @override
  List<Object?> get props => [application, cpe, vendor, product, confidence, status, hosts, cves];
}

class CpeMappingsState extends Equatable {
  const CpeMappingsState({
    this.mappings = const [],
    this.loading = true,
    this.error,
    this.candidates = const {},
    this.searchingFor,
    this.busyId,
    this.filters = const CpeMappingFilters(),
    this.selectedIds = const {},
    this.expandedId,
    this.bulkBusy = false,
    this.bulkResult,
  });

  final List<CpeMapping> mappings;
  final bool loading;
  final String? error;

  /// Dictionary results, keyed by the mapping they were searched for. Kept per row so opening a
  /// second row does not wipe what the first was showing.
  final Map<String, List<CpeCandidate>> candidates;

  final String? searchingFor;

  /// The row a confirm or dismiss is in flight for, so only that row's buttons go busy.
  final String? busyId;

  final CpeMappingFilters filters;

  /// What the checkboxes have ticked, by mapping id.
  ///
  /// Held by id rather than by row so it survives the re-sort a confirmation causes — the server
  /// puts suggested rows first, so acting on a selection moves its members — and pruned on every
  /// refetch, since a subject that has gone must not stay silently ticked.
  final Set<String> selectedIds;

  /// The one row whose detail is open. One at a time, like the Applications screen's instructions
  /// panel: the detail carries a dictionary search a reviewer works in, and several open at once
  /// is what made this queue a page of stacked panels rather than a table.
  final String? expandedId;

  final bool bulkBusy;

  /// What the last bulk Confirm or Clear did, kept until dismissed. The skipped rows are the
  /// point: "12 of 40" without them is a silent partial success.
  final BulkMappingResult? bulkResult;

  /// The rows the filters leave, in the server's order — most widely installed first.
  List<CpeMapping> get visibleMappings =>
      mappings.where(filters.matches).toList(growable: false);

  /// Only ever the visible ones: acting on a selection the filters have hidden is how somebody
  /// confirms a mapping they never read.
  List<String> get actionableIds =>
      [for (final mapping in visibleMappings) if (selectedIds.contains(mapping.id)) mapping.id];

  CpeMappingsState copyWith({
    List<CpeMapping>? mappings,
    bool? loading,
    String? error,
    Map<String, List<CpeCandidate>>? candidates,
    String? searchingFor,
    String? busyId,
    CpeMappingFilters? filters,
    Set<String>? selectedIds,
    String? expandedId,
    bool? bulkBusy,
    BulkMappingResult? bulkResult,
    bool clearError = false,
    bool clearSearching = false,
    bool clearBusy = false,
    bool clearExpanded = false,
    bool clearBulkResult = false,
  }) =>
      CpeMappingsState(
        mappings: mappings ?? this.mappings,
        loading: loading ?? this.loading,
        error: clearError ? null : (error ?? this.error),
        candidates: candidates ?? this.candidates,
        searchingFor: clearSearching ? null : (searchingFor ?? this.searchingFor),
        busyId: clearBusy ? null : (busyId ?? this.busyId),
        filters: filters ?? this.filters,
        selectedIds: selectedIds ?? this.selectedIds,
        expandedId: clearExpanded ? null : (expandedId ?? this.expandedId),
        bulkBusy: bulkBusy ?? this.bulkBusy,
        bulkResult: clearBulkResult ? null : (bulkResult ?? this.bulkResult),
      );

  @override
  List<Object?> get props => [
        mappings,
        loading,
        error,
        candidates,
        searchingFor,
        busyId,
        filters,
        selectedIds,
        expandedId,
        bulkBusy,
        bulkResult,
      ];
}

class CpeMappingsBloc extends Bloc<CpeMappingsEvent, CpeMappingsState> {
  CpeMappingsBloc({
    required GetCpeMappings getMappings,
    required SearchCpeDictionary searchDictionary,
    required ConfirmCpeMapping confirmMapping,
    required MarkCpeMappingNotApplicable markNotApplicable,
    required ResetCpeMapping resetMapping,
    required ConfirmCpeMappings confirmMappings,
    required ResetCpeMappings resetMappings,
  })  : _getMappings = getMappings,
        _searchDictionary = searchDictionary,
        _confirmMapping = confirmMapping,
        _markNotApplicable = markNotApplicable,
        _resetMapping = resetMapping,
        _confirmMappings = confirmMappings,
        _resetMappings = resetMappings,
        super(const CpeMappingsState()) {
    on<CpeMappingsRequested>(_onRequested);
    on<CpeDictionarySearched>(_onSearch);
    on<CpeMappingConfirmed>(_onConfirm);
    on<CpeMappingDismissed>(_onDismiss);
    on<CpeMappingReset>(_onReset);
    on<CpeMappingFiltersChanged>(_onFiltersChanged);
    on<CpeMappingSelectionToggled>(_onSelectionToggled);
    on<CpeMappingVisibleSelectionToggled>(_onVisibleSelectionToggled);
    on<CpeMappingSelectionCleared>(_onSelectionCleared);
    on<CpeMappingExpansionToggled>(_onExpansionToggled);
    on<CpeMappingsBulkConfirmed>(_onBulkConfirm);
    on<CpeMappingsBulkReset>(_onBulkReset);
    on<CpeMappingsBulkResultDismissed>(_onBulkResultDismissed);
  }

  final GetCpeMappings _getMappings;
  final SearchCpeDictionary _searchDictionary;
  final ConfirmCpeMapping _confirmMapping;
  final MarkCpeMappingNotApplicable _markNotApplicable;
  final ResetCpeMapping _resetMapping;
  final ConfirmCpeMappings _confirmMappings;
  final ResetCpeMappings _resetMappings;

  Future<void> _onRequested(CpeMappingsRequested event, Emitter<CpeMappingsState> emit) async {
    emit(state.copyWith(loading: true, clearError: true));
    try {
      final mappings = await _getMappings();
      emit(state.copyWith(
        mappings: mappings,
        loading: false,
        // A selection is only ever over rows that still exist. Keeping an id whose subject the
        // fleet no longer reports would leave a bulk action acting on something nobody can see,
        // and the server would report it skipped for a reason the screen cannot explain.
        selectedIds: _prune(state.selectedIds, mappings),
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  static Set<String> _prune(Set<String> selected, List<CpeMapping> mappings) {
    if (selected.isEmpty) return selected;
    final live = {for (final mapping in mappings) mapping.id};
    return selected.where(live.contains).toSet();
  }

  void _onFiltersChanged(CpeMappingFiltersChanged event, Emitter<CpeMappingsState> emit) =>
      emit(state.copyWith(filters: event.filters));

  void _onSelectionToggled(CpeMappingSelectionToggled event, Emitter<CpeMappingsState> emit) {
    final next = {...state.selectedIds};
    if (!next.remove(event.id)) next.add(event.id);
    emit(state.copyWith(selectedIds: next));
  }

  void _onVisibleSelectionToggled(
      CpeMappingVisibleSelectionToggled event, Emitter<CpeMappingsState> emit) {
    final visible = {for (final mapping in state.visibleMappings) mapping.id};
    emit(state.copyWith(
      selectedIds: event.selected
          ? {...state.selectedIds, ...visible}
          // Only the visible ones are unticked: a filter narrowed to one vendor must not throw
          // away a selection made under the previous one.
          : state.selectedIds.where((id) => !visible.contains(id)).toSet(),
    ));
  }

  void _onSelectionCleared(CpeMappingSelectionCleared event, Emitter<CpeMappingsState> emit) =>
      emit(state.copyWith(selectedIds: const {}));

  void _onExpansionToggled(CpeMappingExpansionToggled event, Emitter<CpeMappingsState> emit) =>
      emit(state.expandedId == event.id
          ? state.copyWith(clearExpanded: true)
          : state.copyWith(expandedId: event.id));

  Future<void> _onBulkConfirm(
      CpeMappingsBulkConfirmed event, Emitter<CpeMappingsState> emit) async {
    await _bulk(emit, _confirmMappings.call);
  }

  Future<void> _onBulkReset(CpeMappingsBulkReset event, Emitter<CpeMappingsState> emit) async {
    await _bulk(emit, _resetMappings.call);
  }

  /// Runs one bulk action over the ticked *visible* rows and re-reads the queue.
  ///
  /// The selection is dropped afterwards whatever happened, including on failure: leaving forty
  /// rows ticked beside a result that says twelve were confirmed invites the reviewer to press the
  /// button again, which is how the same decision gets made twice.
  Future<void> _bulk(
    Emitter<CpeMappingsState> emit,
    Future<BulkMappingResult> Function(List<String> ids) action,
  ) async {
    final ids = state.actionableIds;
    if (ids.isEmpty) return;

    emit(state.copyWith(bulkBusy: true, clearError: true, clearBulkResult: true));
    try {
      final result = await action(ids);
      emit(state.copyWith(
        mappings: await _getMappings(),
        bulkBusy: false,
        bulkResult: result,
        selectedIds: const {},
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(bulkBusy: false, error: error.message, selectedIds: const {}));
    }
  }

  void _onBulkResultDismissed(
          CpeMappingsBulkResultDismissed event, Emitter<CpeMappingsState> emit) =>
      emit(state.copyWith(clearBulkResult: true));

  Future<void> _onSearch(CpeDictionarySearched event, Emitter<CpeMappingsState> emit) async {
    emit(state.copyWith(searchingFor: event.mappingId, clearError: true));
    try {
      final results = await _searchDictionary(event.keyword);
      emit(state.copyWith(
        candidates: {...state.candidates, event.mappingId: results},
        clearSearching: true,
      ));
    } on ApiException catch (error) {
      // The dictionary search shares this server's NVD rate allowance with the background run, so
      // a slow or refused answer here is a real and explicable outcome rather than a bug.
      emit(state.copyWith(error: error.message, clearSearching: true));
    }
  }

  Future<void> _onConfirm(CpeMappingConfirmed event, Emitter<CpeMappingsState> emit) async {
    emit(state.copyWith(busyId: event.id, clearError: true));
    try {
      await _confirmMapping(id: event.id, vendor: event.vendor, product: event.product);
      emit(state.copyWith(mappings: await _getMappings(), clearBusy: true));
    } on ApiException catch (error) {
      // Includes the 409 for a vendor and product NVD's dictionary does not contain, which is the
      // check that stops a typo attributing another product's CVEs to this one.
      emit(state.copyWith(error: error.message, clearBusy: true));
    }
  }

  Future<void> _onDismiss(CpeMappingDismissed event, Emitter<CpeMappingsState> emit) async {
    emit(state.copyWith(busyId: event.id, clearError: true));
    try {
      await _markNotApplicable(id: event.id);
      emit(state.copyWith(mappings: await _getMappings(), clearBusy: true));
    } on ApiException catch (error) {
      emit(state.copyWith(error: error.message, clearBusy: true));
    }
  }

  Future<void> _onReset(CpeMappingReset event, Emitter<CpeMappingsState> emit) async {
    emit(state.copyWith(busyId: event.id, clearError: true));
    try {
      await _resetMapping(event.id);
      emit(state.copyWith(mappings: await _getMappings(), clearBusy: true));
    } on ApiException catch (error) {
      emit(state.copyWith(error: error.message, clearBusy: true));
    }
  }
}
