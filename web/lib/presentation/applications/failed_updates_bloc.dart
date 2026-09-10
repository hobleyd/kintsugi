import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/patch_failure.dart';
import '../../domain/usecases/patch_failure_usecases.dart';

sealed class FailedUpdatesEvent extends Equatable {
  const FailedUpdatesEvent();

  @override
  List<Object?> get props => const [];
}

final class FailedUpdatesRequested extends FailedUpdatesEvent {
  const FailedUpdatesRequested({this.showSpinner = true});

  /// False for the reload that follows a save, a sign or a dismiss: the table is already on screen
  /// and blanking it to a spinner for one round trip is a flicker, not feedback.
  final bool showSpinner;

  @override
  List<Object?> get props => [showSpinner];
}

final class FailedUpdatesFiltersChanged extends FailedUpdatesEvent {
  const FailedUpdatesFiltersChanged(this.filters);

  final FailedUpdateFilters filters;

  @override
  List<Object?> get props => [filters];
}

/// Expands (or collapses) one row's fix panel. Only one at a time, like the Applications screen:
/// two open AI panels would be two background refreshes polling at once with nothing saying which
/// result belonged to which.
final class FailedUpdateRowExpansionToggled extends FailedUpdatesEvent {
  const FailedUpdateRowExpansionToggled(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

final class FailedUpdateDismissed extends FailedUpdatesEvent {
  const FailedUpdateDismissed(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

/// What the table is narrowed to. Defaults to outstanding failures, which is the queue; the settled
/// ones are kept and reachable rather than deleted, because "this used to fail and then patched"
/// is the answer to the question somebody asks next.
class FailedUpdateFilters extends Equatable {
  const FailedUpdateFilters({
    this.search = '',
    this.hostName = 'all',
    this.resolutionKey = 'outstanding',
  });

  final String search;
  final String hostName;

  /// One of `outstanding`, `settled`, `all`.
  final String resolutionKey;

  bool get isActive => search.isNotEmpty || hostName != 'all' || resolutionKey != 'outstanding';

  FailedUpdateFilters copyWith({String? search, String? hostName, String? resolutionKey}) =>
      FailedUpdateFilters(
        search: search ?? this.search,
        hostName: hostName ?? this.hostName,
        resolutionKey: resolutionKey ?? this.resolutionKey,
      );

  bool matches(PatchFailure failure) {
    if (!_matchesResolution(failure)) return false;
    if (hostName != 'all' && failure.hostname.toLowerCase() != hostName.toLowerCase()) return false;
    if (search.isEmpty) return true;

    final needle = search.toLowerCase();
    return failure.applicationName.toLowerCase().contains(needle) ||
        failure.hostname.toLowerCase().contains(needle) ||
        // The output too: "Permission denied" across four hosts is one problem, and finding it that
        // way is most of what this screen is for.
        failure.details.toLowerCase().contains(needle);
  }

  bool _matchesResolution(PatchFailure failure) => switch (resolutionKey) {
        'outstanding' => failure.isOutstanding,
        'settled' => !failure.isOutstanding,
        _ => true,
      };

  @override
  List<Object?> get props => [search, hostName, resolutionKey];
}

class FailedUpdatesState extends Equatable {
  const FailedUpdatesState({
    this.loading = true,
    this.failures = const [],
    this.filters = const FailedUpdateFilters(),
    this.expandedId,
    this.dismissingIds = const {},
    this.error,
    this.notice,
  });

  final bool loading;
  final List<PatchFailure> failures;
  final FailedUpdateFilters filters;
  final String? expandedId;

  /// Rows with a dismiss in flight, so the button can dim rather than being pressable twice.
  final Set<String> dismissingIds;

  final String? error;
  final String? notice;

  List<PatchFailure> get visibleRows =>
      failures.where(filters.matches).toList(growable: false);

  /// Every host that has reported a failure, for the host filter. Taken from the whole set rather
  /// than the visible one, so choosing a host never empties the list it was chosen from.
  List<String> get allHostNames {
    final names = failures.map((f) => f.hostname).toSet().toList()
      ..sort((a, b) => a.toLowerCase().compareTo(b.toLowerCase()));
    return names;
  }

  int get outstandingCount => failures.where((f) => f.isOutstanding).length;

  FailedUpdatesState copyWith({
    bool? loading,
    List<PatchFailure>? failures,
    FailedUpdateFilters? filters,
    String? expandedId,
    Set<String>? dismissingIds,
    String? error,
    String? notice,
    bool clearExpanded = false,
    bool clearMessages = false,
  }) =>
      FailedUpdatesState(
        loading: loading ?? this.loading,
        failures: failures ?? this.failures,
        filters: filters ?? this.filters,
        expandedId: clearExpanded ? null : (expandedId ?? this.expandedId),
        dismissingIds: dismissingIds ?? this.dismissingIds,
        error: clearMessages ? null : (error ?? this.error),
        notice: clearMessages ? null : (notice ?? this.notice),
      );

  @override
  List<Object?> get props => [loading, failures, filters, expandedId, dismissingIds, error, notice];
}

/// The Failed Updates screen.
///
/// No polling, unlike the Applications screen: nothing here runs in the background on the server.
/// A failure arrives when an agent's next patch cycle reports one, which is minutes to hours away,
/// and the one thing that *does* change while this screen is open — an AI repair — is polled by the
/// fix panel itself, which then asks for a reload.
class FailedUpdatesBloc extends Bloc<FailedUpdatesEvent, FailedUpdatesState> {
  FailedUpdatesBloc({
    required GetPatchFailures getFailures,
    required DismissPatchFailure dismissFailure,
  })  : _getFailures = getFailures,
        _dismissFailure = dismissFailure,
        super(const FailedUpdatesState()) {
    on<FailedUpdatesRequested>(_onRequested);
    on<FailedUpdatesFiltersChanged>((event, emit) => emit(state.copyWith(filters: event.filters)));
    on<FailedUpdateRowExpansionToggled>(
      (event, emit) => emit(
        state.expandedId == event.id
            ? state.copyWith(clearExpanded: true)
            : state.copyWith(expandedId: event.id),
      ),
    );
    on<FailedUpdateDismissed>(_onDismissed);
  }

  final GetPatchFailures _getFailures;
  final DismissPatchFailure _dismissFailure;

  Future<void> _onRequested(FailedUpdatesRequested event, Emitter<FailedUpdatesState> emit) async {
    if (event.showSpinner) emit(state.copyWith(loading: true, clearMessages: true));

    try {
      emit(state.copyWith(loading: false, failures: await _getFailures(), error: ''));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onDismissed(FailedUpdateDismissed event, Emitter<FailedUpdatesState> emit) async {
    emit(state.copyWith(dismissingIds: {...state.dismissingIds, event.id}, clearMessages: true));

    try {
      await _dismissFailure(event.id);
      final failures = await _getFailures();
      emit(state.copyWith(
        failures: failures,
        dismissingIds: {...state.dismissingIds}..remove(event.id),
        notice: 'Dismissed.',
        // The row it was open on may no longer be in view, and a panel left expanded against a
        // hidden row is a refresh polling for something nobody can see.
        clearExpanded: state.expandedId == event.id,
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(
        dismissingIds: {...state.dismissingIds}..remove(event.id),
        error: 'Could not dismiss: ${error.message}',
      ));
    }
  }
}

/// The label the resolution filter offers for each option.
const failedUpdateResolutionOptions = <String, String>{
  'outstanding': 'Outstanding',
  'settled': 'Settled',
  'all': 'All',
};

/// What a settled row's chip says. Outstanding rows carry the failure count instead, which says
/// more.
String failedUpdateResolutionLabel(PatchFailureResolution resolution) => resolution.label;
