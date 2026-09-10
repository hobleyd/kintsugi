import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/patch_failure.dart';
import '../../domain/usecases/patch_failure_usecases.dart';
import 'instructions_panel_bloc.dart';

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

/// A repaired script was signed from this row's fix panel. The server has already taken the
/// failures that script was causing off the queue (see `SignUpgradePathScriptCommandHandler`), so
/// this re-reads the list, closes the panel, and says what happened — including when the clearing
/// reached other hosts failing on the same script, which the operator did not ask for by name.
final class FailedUpdateScriptSigned extends FailedUpdatesEvent {
  const FailedUpdateScriptSigned(this.id, this.outcome);

  final String id;
  final SignedScriptOutcome outcome;

  @override
  List<Object?> get props => [id, outcome];
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
    this.noticeLinkUrl,
  });

  final bool loading;
  final List<PatchFailure> failures;
  final FailedUpdateFilters filters;
  final String? expandedId;

  /// Rows with a dismiss in flight, so the button can dim rather than being pressable twice.
  final Set<String> dismissingIds;

  final String? error;
  final String? notice;

  /// A link rendered beside [notice] — the approval pull request a signature opened. It lives here
  /// rather than in the fix panel because signing closes that panel in the same frame, and the pull
  /// request is the durable record of the review.
  final String? noticeLinkUrl;

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
    String? noticeLinkUrl,
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
        // Tied to the notice it belongs to rather than carried forward on its own: a link left
        // behind by a cleared notice would point at the pull request for something else.
        noticeLinkUrl: clearMessages ? null : (noticeLinkUrl ?? this.noticeLinkUrl),
      );

  @override
  List<Object?> get props =>
      [loading, failures, filters, expandedId, dismissingIds, error, notice, noticeLinkUrl];
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
    on<FailedUpdatesFiltersChanged>(
      // clearMessages for the same reason a reload does it: the banner describes an action, and
      // narrowing the table is a different one.
      (event, emit) => emit(state.copyWith(filters: event.filters, clearMessages: true)),
    );
    on<FailedUpdateRowExpansionToggled>(
      (event, emit) => emit(
        state.expandedId == event.id
            ? state.copyWith(clearExpanded: true)
            : state.copyWith(expandedId: event.id),
      ),
    );
    on<FailedUpdateScriptSigned>(_onScriptSigned);
    on<FailedUpdateDismissed>(_onDismissed);
  }

  final GetPatchFailures _getFailures;
  final DismissPatchFailure _dismissFailure;

  Future<void> _onRequested(FailedUpdatesRequested event, Emitter<FailedUpdatesState> emit) async {
    // Cleared whether or not this reload shows a spinner. The banner describes one action, and the
    // reload that follows a save is a *different* action — leaving it up meant "Signed the repaired
    // script for Ollama" stayed on screen through every later filter change and background reload,
    // announcing something three interactions old as though it had just happened. The events that
    // set a banner (`_onScriptSigned`, `_onDismissed`) read the list themselves rather than going
    // through this one, so they are not clearing their own message.
    emit(state.copyWith(loading: event.showSpinner ? true : null, clearMessages: true));

    try {
      emit(state.copyWith(loading: false, failures: await _getFailures()));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onScriptSigned(FailedUpdateScriptSigned event, Emitter<FailedUpdatesState> emit) async {
    // Read before the reload, since the row is about to leave the list — and without
    // `firstOrNull`, which is `package:collection`'s and reaches this file only through a
    // transitive export that nothing here depends on directly.
    final matching = state.failures.where((failure) => failure.id == event.id);
    final application = matching.isEmpty ? null : matching.first.applicationName;

    try {
      emit(state.copyWith(
        failures: await _getFailures(),
        // The row this panel belonged to has just left the default view, and a panel expanded
        // against a row nobody can see is a background refresh polling for nothing.
        clearExpanded: true,
        notice: _describeSigning(application, event.outcome),
        noticeLinkUrl: event.outcome.approvalPullRequestUrl,
      ));
    } on ApiException catch (error) {
      // The signature landed regardless — this is only the re-read. Say so rather than implying
      // the fix did not save.
      emit(state.copyWith(
        clearExpanded: true,
        error: 'Signed, but the list could not be re-read: ${error.message}',
      ));
    }
  }

  /// What the screen says after a repair is signed.
  ///
  /// Three things, and each is here because the panel that would otherwise have said it is closing
  /// in the same frame. The count, because clearing the row the operator had open also clears every
  /// other host failing on that same script — a wider action than they asked for by name, and one
  /// that should be visible rather than silent. That the fix is unproven, because nothing here
  /// claims it worked. And what became of the approval, since the pull request is the durable
  /// record of the review and its link went down with the panel.
  static String _describeSigning(String? application, SignedScriptOutcome outcome) {
    final subject = application ?? 'this application';
    final cleared = switch (outcome.clearedPatchFailures) {
      0 => 'Nothing was outstanding to clear',
      1 => 'Cleared its failure',
      final count => 'Cleared $count failures across the hosts running it',
    };

    final approval =
        outcome.approvalDescription.isEmpty ? '' : ' ${outcome.approvalDescription}';

    return 'Signed the repaired script for $subject. $cleared — it comes back if the next patch '
        'cycle still fails.$approval';
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
