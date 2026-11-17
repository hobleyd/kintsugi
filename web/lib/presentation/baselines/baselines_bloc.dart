import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../domain/entities/baseline.dart';
import '../../domain/usecases/baseline_usecases.dart';

/// How often the Baselines screens re-read. A host reports at its hourly check-in, so nothing here
/// moves faster than that; states are value-compared, so a poll that finds nothing new rebuilds
/// nothing.
const baselinesPollInterval = Duration(seconds: 30);

/// The three Baselines screens share one event set: load (with or without a spinner), and the three
/// writes. Each bloc handles the ones its screen offers.
sealed class BaselinesEvent extends Equatable {
  const BaselinesEvent();

  @override
  List<Object?> get props => const [];
}

final class BaselinesLoadRequested extends BaselinesEvent {
  const BaselinesLoadRequested({this.showSpinner = true});

  /// False for a background poll, so a refresh does not blank what is already on screen.
  final bool showSpinner;

  @override
  List<Object?> get props => [showSpinner];
}

/// Pasted JSON: a new baseline on the list screen, a new version on a baseline's own screen.
final class BaselineImportRequested extends BaselinesEvent {
  const BaselineImportRequested(this.json);

  final String json;

  @override
  List<Object?> get props => [json];
}

final class BaselineSignRequested extends BaselinesEvent {
  const BaselineSignRequested(this.version);

  final BaselineVersion version;

  @override
  List<Object?> get props => [version];
}

final class BaselineAssignRequested extends BaselinesEvent {
  const BaselineAssignRequested({required this.allWindowsHosts, required this.hostNames});

  final bool allWindowsHosts;
  final List<String> hostNames;

  @override
  List<Object?> get props => [allWindowsHosts, hostNames];
}

/// One screen's data of type [T], and what is in flight.
final class BaselinesState<T> extends Equatable {
  const BaselinesState({this.data, this.loading = true, this.busy = false, this.error, this.notice});

  final T? data;
  final bool loading;

  /// A write is in flight, so its button is disabled.
  final bool busy;
  final String? error;
  final String? notice;

  BaselinesState<T> copyWith({
    T? data,
    bool? loading,
    bool? busy,
    String? error,
    String? notice,
    bool clearError = false,
    bool clearNotice = false,
  }) =>
      BaselinesState<T>(
        data: data ?? this.data,
        loading: loading ?? this.loading,
        busy: busy ?? this.busy,
        error: clearError ? null : (error ?? this.error),
        notice: clearNotice ? null : (notice ?? this.notice),
      );

  @override
  List<Object?> get props => [data, loading, busy, error, notice];
}

/// Loads [T] and polls it, and runs the writes a screen offers — each followed by a reload, since
/// every write changes what the screen shows.
abstract class BaselineDataBloc<T> extends Bloc<BaselinesEvent, BaselinesState<T>>
    with Polling<BaselinesEvent, BaselinesState<T>> {
  BaselineDataBloc() : super(BaselinesState<T>()) {
    on<BaselinesLoadRequested>(_onLoad);
    startPolling(baselinesPollInterval, const BaselinesLoadRequested(showSpinner: false));
  }

  Future<T> load();

  Future<void> _onLoad(BaselinesLoadRequested event, Emitter<BaselinesState<T>> emit) async {
    if (event.showSpinner) emit(state.copyWith(loading: true, clearError: true));

    try {
      emit(state.copyWith(data: await load(), loading: false, clearError: true));
    } on ApiException catch (error) {
      // A failed poll leaves what is on screen alone and says so.
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  /// Runs one write, then reloads. [done] is the notice shown when it worked.
  Future<void> write(Emitter<BaselinesState<T>> emit, Future<void> Function() action, String done) async {
    emit(state.copyWith(busy: true, clearError: true, clearNotice: true));
    try {
      await action();
      emit(state.copyWith(busy: false, notice: done));
      add(const BaselinesLoadRequested(showSpinner: false));
    } on ApiException catch (error) {
      emit(state.copyWith(busy: false, error: error.message));
    } on FormatException catch (error) {
      emit(state.copyWith(busy: false, error: 'That is not valid JSON: ${error.message}'));
    }
  }
}

/// The list of baselines.
class BaselinesBloc extends BaselineDataBloc<List<BaselineSummary>> {
  BaselinesBloc({required GetBaselines getBaselines, required ImportBaseline importBaseline})
      : _getBaselines = getBaselines,
        _importBaseline = importBaseline {
    on<BaselineImportRequested>(
      (event, emit) => write(emit, () => _importBaseline(event.json), 'Baseline created, unsigned. Open it to review and sign.'),
    );
  }

  final GetBaselines _getBaselines;
  final ImportBaseline _importBaseline;

  @override
  Future<List<BaselineSummary>> load() => _getBaselines();
}

/// One baseline: review and sign a version, assign it, add a version.
class BaselineBloc extends BaselineDataBloc<BaselineDetail> {
  BaselineBloc({
    required this.baselineId,
    required GetBaseline getBaseline,
    required ImportBaseline importBaseline,
    required SignBaselineVersion signVersion,
    required AssignBaseline assignBaseline,
  })  : _getBaseline = getBaseline,
        _importBaseline = importBaseline,
        _signVersion = signVersion,
        _assignBaseline = assignBaseline {
    on<BaselineImportRequested>(
      (event, emit) => write(
        emit,
        () => _importBaseline(event.json, baselineId: baselineId),
        'New version created, unsigned. Agents keep running the signed one until it is signed.',
      ),
    );
    on<BaselineSignRequested>(
      (event, emit) => write(
        emit,
        () => _signVersion(event.version),
        'Version ${event.version.number} signed. Assigned hosts read it at their next check-in.',
      ),
    );
    on<BaselineAssignRequested>(
      (event, emit) => write(
        emit,
        () => _assignBaseline(baselineId, allWindowsHosts: event.allWindowsHosts, hostNames: event.hostNames),
        'Assignment saved.',
      ),
    );
  }

  final String baselineId;
  final GetBaseline _getBaseline;
  final ImportBaseline _importBaseline;
  final SignBaselineVersion _signVersion;
  final AssignBaseline _assignBaseline;

  @override
  Future<BaselineDetail> load() => _getBaseline(baselineId);
}

/// One host's item states and facts against one baseline.
class BaselineHostBloc extends BaselineDataBloc<BaselineHostDetail> {
  BaselineHostBloc({required this.baselineId, required this.hostId, required GetBaselineHost getBaselineHost})
      : _getBaselineHost = getBaselineHost;

  final String baselineId;
  final String hostId;
  final GetBaselineHost _getBaselineHost;

  @override
  Future<BaselineHostDetail> load() => _getBaselineHost(baselineId, hostId);
}
