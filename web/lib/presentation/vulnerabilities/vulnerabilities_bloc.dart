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

class CpeMappingsState extends Equatable {
  const CpeMappingsState({
    this.mappings = const [],
    this.loading = true,
    this.error,
    this.candidates = const {},
    this.searchingFor,
    this.busyId,
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

  CpeMappingsState copyWith({
    List<CpeMapping>? mappings,
    bool? loading,
    String? error,
    Map<String, List<CpeCandidate>>? candidates,
    String? searchingFor,
    String? busyId,
    bool clearError = false,
    bool clearSearching = false,
    bool clearBusy = false,
  }) =>
      CpeMappingsState(
        mappings: mappings ?? this.mappings,
        loading: loading ?? this.loading,
        error: clearError ? null : (error ?? this.error),
        candidates: candidates ?? this.candidates,
        searchingFor: clearSearching ? null : (searchingFor ?? this.searchingFor),
        busyId: clearBusy ? null : (busyId ?? this.busyId),
      );

  @override
  List<Object?> get props => [mappings, loading, error, candidates, searchingFor, busyId];
}

class CpeMappingsBloc extends Bloc<CpeMappingsEvent, CpeMappingsState> {
  CpeMappingsBloc({
    required GetCpeMappings getMappings,
    required SearchCpeDictionary searchDictionary,
    required ConfirmCpeMapping confirmMapping,
    required MarkCpeMappingNotApplicable markNotApplicable,
    required ResetCpeMapping resetMapping,
  })  : _getMappings = getMappings,
        _searchDictionary = searchDictionary,
        _confirmMapping = confirmMapping,
        _markNotApplicable = markNotApplicable,
        _resetMapping = resetMapping,
        super(const CpeMappingsState()) {
    on<CpeMappingsRequested>(_onRequested);
    on<CpeDictionarySearched>(_onSearch);
    on<CpeMappingConfirmed>(_onConfirm);
    on<CpeMappingDismissed>(_onDismiss);
    on<CpeMappingReset>(_onReset);
  }

  final GetCpeMappings _getMappings;
  final SearchCpeDictionary _searchDictionary;
  final ConfirmCpeMapping _confirmMapping;
  final MarkCpeMappingNotApplicable _markNotApplicable;
  final ResetCpeMapping _resetMapping;

  Future<void> _onRequested(CpeMappingsRequested event, Emitter<CpeMappingsState> emit) async {
    emit(state.copyWith(loading: true, clearError: true));
    try {
      emit(state.copyWith(mappings: await _getMappings(), loading: false));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

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
