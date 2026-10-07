import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';

sealed class AiRoutingEvent extends Equatable {
  const AiRoutingEvent();

  @override
  List<Object?> get props => const [];
}

final class AiRoutingRequested extends AiRoutingEvent {
  const AiRoutingRequested();
}

final class AiCatalogRefreshRequested extends AiRoutingEvent {
  const AiCatalogRefreshRequested();
}

final class AiConnectionSaveRequested extends AiRoutingEvent {
  const AiConnectionSaveRequested(this.draft);

  final AiConnectionDraft draft;
}

final class AiConnectionDeleteRequested extends AiRoutingEvent {
  const AiConnectionDeleteRequested(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

final class AiRouteSaveRequested extends AiRoutingEvent {
  const AiRouteSaveRequested(this.feature, this.connectionId, this.model);

  final AiFeature feature;
  final String connectionId;
  final String model;

  @override
  List<Object?> get props => [feature, connectionId, model];
}

final class AiRouteClearRequested extends AiRoutingEvent {
  const AiRouteClearRequested(this.feature);

  final AiFeature feature;

  @override
  List<Object?> get props => [feature];
}

/// Tests a stored connection with the model a feature is routed to.
final class AiRouteTestRequested extends AiRoutingEvent {
  const AiRouteTestRequested(this.feature, this.connectionId, this.model);

  final AiFeature feature;
  final String connectionId;
  final String model;

  @override
  List<Object?> get props => [feature, connectionId, model];
}

final class AiWebSearchSaveRequested extends AiRoutingEvent {
  const AiWebSearchSaveRequested({required this.backend, required this.apiKey, required this.clearApiKey, required this.baseUrl});

  final WebSearchBackend backend;
  final String? apiKey;
  final bool clearApiKey;
  final String? baseUrl;

  @override
  List<Object?> get props => [backend, apiKey, clearApiKey, baseUrl];
}

class AiRoutingState extends Equatable {
  const AiRoutingState({
    this.routing,
    this.catalog = AiCatalog.empty,
    this.webSearch,
    this.loading = false,
    this.busy = false,
    this.error,
    this.notice,
    this.tests = const {},
    this.testing = const {},
  });

  final AiRouting? routing;
  final AiCatalog catalog;

  /// The settings row as the last web-search save returned it; null until one happens, when the
  /// screen reads the AI Agent bloc's copy instead.
  final AiAgentSettings? webSearch;
  final bool loading;
  final bool busy;
  final String? error;
  final String? notice;

  /// The last test per feature, so each route row shows its own result.
  final Map<AiFeature, AiConnectionTestResult> tests;
  final Set<AiFeature> testing;

  AiRoutingState copyWith({
    AiRouting? routing,
    AiCatalog? catalog,
    AiAgentSettings? webSearch,
    bool? loading,
    bool? busy,
    String? error,
    bool clearError = false,
    String? notice,
    bool clearNotice = false,
    Map<AiFeature, AiConnectionTestResult>? tests,
    Set<AiFeature>? testing,
  }) =>
      AiRoutingState(
        routing: routing ?? this.routing,
        catalog: catalog ?? this.catalog,
        webSearch: webSearch ?? this.webSearch,
        loading: loading ?? this.loading,
        busy: busy ?? this.busy,
        error: clearError ? null : error ?? this.error,
        notice: clearNotice ? null : notice ?? this.notice,
        tests: tests ?? this.tests,
        testing: testing ?? this.testing,
      );

  @override
  List<Object?> get props => [routing, catalog, webSearch, loading, busy, error, notice, tests, testing];
}

/// Routed mode's half of the AI Agent screen. Separate from `AiAgentBloc` because nothing here is
/// part of that form's single Save: every connection, route and search setting saves on its own.
class AiRoutingBloc extends Bloc<AiRoutingEvent, AiRoutingState> {
  AiRoutingBloc(this._routing) : super(const AiRoutingState()) {
    on<AiRoutingRequested>(_onRequested);
    on<AiCatalogRefreshRequested>((_, emit) => _act(emit, () async {
          emit(state.copyWith(catalog: await _routing.catalog(refresh: true)));
        }));
    on<AiConnectionSaveRequested>((event, emit) => _act(emit, () async {
          await _routing.saveConnection(event.draft);
          emit(state.copyWith(routing: await _routing.read(), notice: 'Connection "${event.draft.name}" saved.'));
        }));
    on<AiConnectionDeleteRequested>((event, emit) => _act(emit, () async {
          await _routing.deleteConnection(event.id);
          emit(state.copyWith(routing: await _routing.read(), notice: 'Connection deleted.'));
        }));
    on<AiRouteSaveRequested>((event, emit) => _act(emit, () async {
          await _routing.setRoute(event.feature, event.connectionId, event.model);
          emit(state.copyWith(routing: await _routing.read(), notice: '${event.feature.label} routed to ${event.model}.'));
        }));
    on<AiRouteClearRequested>((event, emit) => _act(emit, () async {
          await _routing.clearRoute(event.feature);
          emit(state.copyWith(routing: await _routing.read(), notice: '${event.feature.label} now falls back.'));
        }));
    on<AiWebSearchSaveRequested>((event, emit) => _act(emit, () async {
          final saved = await _routing.updateWebSearch(
              backend: event.backend, apiKey: event.apiKey, clearApiKey: event.clearApiKey, baseUrl: event.baseUrl);
          emit(state.copyWith(webSearch: saved, notice: 'Web search saved.'));
        }));
    on<AiRouteTestRequested>(_onTest);
  }

  final ManageAiRouting _routing;

  Future<void> _onRequested(AiRoutingRequested event, Emitter<AiRoutingState> emit) async {
    emit(state.copyWith(loading: true, clearError: true));
    try {
      // The catalog is fetched beside the routing, not before it: a server that cannot reach
      // models.dev still has working connections to show.
      final results = await Future.wait([_routing.read(), _routing.catalog()]);
      emit(state.copyWith(routing: results[0] as AiRouting, catalog: results[1] as AiCatalog, loading: false));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onTest(AiRouteTestRequested event, Emitter<AiRoutingState> emit) async {
    emit(state.copyWith(testing: {...state.testing, event.feature}, clearError: true));
    try {
      final result = await _routing.testConnection(event.connectionId, event.model);
      emit(state.copyWith(
        tests: {...state.tests, event.feature: result},
        testing: {...state.testing}..remove(event.feature),
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(error: error.message, testing: {...state.testing}..remove(event.feature)));
    }
  }

  Future<void> _act(Emitter<AiRoutingState> emit, Future<void> Function() action) async {
    emit(state.copyWith(busy: true, clearError: true, clearNotice: true));
    try {
      await action();
      emit(state.copyWith(busy: false));
    } on ApiException catch (error) {
      emit(state.copyWith(busy: false, error: error.message));
    }
  }
}
