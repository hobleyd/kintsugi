import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';
import '../../domain/repositories/repositories.dart';
import '../../domain/usecases/settings_usecases.dart';

sealed class AiAgentEvent extends Equatable {
  const AiAgentEvent();

  @override
  List<Object?> get props => const [];
}

final class AiAgentSettingsRequested extends AiAgentEvent {
  const AiAgentSettingsRequested();
}

final class AiAgentSettingsSaveRequested extends AiAgentEvent {
  const AiAgentSettingsSaveRequested(this.update);

  final AiAgentSettingsUpdate update;

  @override
  List<Object?> get props => [update.provider, update.model, update.baseUrl, update.isEnabled];
}

final class AiAgentSettingsEdited extends AiAgentEvent {
  const AiAgentSettingsEdited();
}

/// Asks an Ollama endpoint what it is serving, so the model is a choice rather than a string to
/// get exactly right by hand.
final class OllamaModelsRequested extends AiAgentEvent {
  const OllamaModelsRequested(this.baseUrl);

  final String baseUrl;

  @override
  List<Object?> get props => [baseUrl];
}

/// Checks that a `goose serve` endpoint is reachable *from the server*, which is the only place it
/// matters — the AI call is made there, not here.
final class GooseCliStatusRequested extends AiAgentEvent {
  const GooseCliStatusRequested(this.endpoint);

  final String? endpoint;

  @override
  List<Object?> get props => [endpoint];
}

/// Checks that the Claude Agent SDK is usable *from the server* — the `claude` binary is installed
/// there and the stored OAuth token still authenticates. It carries no argument because it can't:
/// the token is never sent to this client, so the probe reads the saved one, which also means it
/// describes what the server will actually run with rather than what is currently on screen.
final class ClaudeAgentSdkStatusRequested extends AiAgentEvent {
  const ClaudeAgentSdkStatusRequested();
}

final class AiAgentState extends Equatable {
  const AiAgentState({
    this.value,
    this.loading = true,
    this.saving = false,
    this.saved = false,
    this.error,
    this.fieldErrors = const {},
    this.ollamaModels = const [],
    this.probeMessage,
    this.probing = false,
  });

  final AiAgentSettings? value;
  final bool loading;
  final bool saving;
  final bool saved;
  final String? error;
  final Map<String, List<String>> fieldErrors;

  /// What the configured Ollama endpoint is serving. Empty until asked.
  final List<String> ollamaModels;

  /// The outcome of the last provider probe — Ollama's, Goose's or the Claude Agent SDK's. One
  /// field for all of them because only one is on screen at a time: which provider is selected
  /// decides which.
  final String? probeMessage;

  final bool probing;

  String? errorFor(String property) {
    for (final entry in fieldErrors.entries) {
      if (entry.key.toLowerCase() == property.toLowerCase() && entry.value.isNotEmpty) {
        return entry.value.first;
      }
    }
    return null;
  }

  AiAgentState copyWith({
    AiAgentSettings? value,
    bool? loading,
    bool? saving,
    bool? saved,
    String? error,
    Map<String, List<String>>? fieldErrors,
    List<String>? ollamaModels,
    String? probeMessage,
    bool? probing,
    bool clearError = false,
    bool clearProbe = false,
  }) =>
      AiAgentState(
        value: value ?? this.value,
        loading: loading ?? this.loading,
        saving: saving ?? this.saving,
        saved: saved ?? this.saved,
        error: clearError ? null : (error ?? this.error),
        fieldErrors: fieldErrors ?? this.fieldErrors,
        ollamaModels: ollamaModels ?? this.ollamaModels,
        probeMessage: clearProbe ? null : (probeMessage ?? this.probeMessage),
        probing: probing ?? this.probing,
      );

  @override
  List<Object?> get props =>
      [value, loading, saving, saved, error, fieldErrors, ollamaModels, probeMessage, probing];
}

class AiAgentBloc extends Bloc<AiAgentEvent, AiAgentState> {
  AiAgentBloc({
    required GetAiAgentSettings getSettings,
    required UpdateAiAgentSettings updateSettings,
    required GetOllamaModels getOllamaModels,
    required CheckGooseCliStatus checkGooseCliStatus,
    required CheckClaudeAgentSdkStatus checkClaudeAgentSdkStatus,
  })  : _getSettings = getSettings,
        _updateSettings = updateSettings,
        _getOllamaModels = getOllamaModels,
        _checkGooseCliStatus = checkGooseCliStatus,
        _checkClaudeAgentSdkStatus = checkClaudeAgentSdkStatus,
        super(const AiAgentState()) {
    on<AiAgentSettingsRequested>(_onRequested);
    on<AiAgentSettingsSaveRequested>(_onSave);
    on<AiAgentSettingsEdited>(
      (_, emit) => emit(state.copyWith(saved: false, clearError: true, fieldErrors: const {})),
    );
    on<OllamaModelsRequested>(_onOllamaModels);
    on<GooseCliStatusRequested>(_onGooseStatus);
    on<ClaudeAgentSdkStatusRequested>(_onClaudeAgentSdkStatus);
  }

  final GetAiAgentSettings _getSettings;
  final UpdateAiAgentSettings _updateSettings;
  final GetOllamaModels _getOllamaModels;
  final CheckGooseCliStatus _checkGooseCliStatus;
  final CheckClaudeAgentSdkStatus _checkClaudeAgentSdkStatus;

  Future<void> _onRequested(AiAgentSettingsRequested event, Emitter<AiAgentState> emit) async {
    emit(state.copyWith(loading: true, clearError: true));
    try {
      final settings = await _getSettings();
      emit(state.copyWith(value: settings, loading: false));

      // Ollama's model list is the field's only sensible source, so fetch it as soon as there is
      // an endpoint to fetch it from rather than waiting to be asked.
      if (settings.provider == AiProvider.ollama && (settings.baseUrl?.isNotEmpty ?? false)) {
        add(OllamaModelsRequested(settings.baseUrl!));
      } else if (settings.provider == AiProvider.gooseCli) {
        add(GooseCliStatusRequested(settings.baseUrl));
      } else if (settings.provider == AiProvider.claudeAgentSdk) {
        add(const ClaudeAgentSdkStatusRequested());
      }
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onSave(AiAgentSettingsSaveRequested event, Emitter<AiAgentState> emit) async {
    emit(state.copyWith(saving: true, saved: false, clearError: true, fieldErrors: const {}));
    try {
      emit(state.copyWith(value: await _updateSettings(event.update), saving: false, saved: true));
    } on ApiException catch (error) {
      emit(state.copyWith(
        saving: false,
        error: error.validationErrors.isEmpty ? error.message : null,
        fieldErrors: error.validationErrors,
      ));
    }
  }

  Future<void> _onOllamaModels(OllamaModelsRequested event, Emitter<AiAgentState> emit) async {
    if (event.baseUrl.trim().isEmpty) {
      emit(state.copyWith(probeMessage: 'Enter an endpoint URL first.', ollamaModels: const []));
      return;
    }

    emit(state.copyWith(probing: true, probeMessage: 'Loading models…'));
    try {
      final models = await _getOllamaModels(event.baseUrl.trim());
      emit(state.copyWith(
        probing: false,
        ollamaModels: models,
        probeMessage: models.isEmpty
            ? 'No models found on this endpoint.'
            : '${models.length} model(s) found.',
      ));
    } on ApiException {
      emit(state.copyWith(
        probing: false,
        ollamaModels: const [],
        probeMessage: 'Could not reach the Ollama endpoint.',
      ));
    }
  }

  Future<void> _onClaudeAgentSdkStatus(
    ClaudeAgentSdkStatusRequested event,
    Emitter<AiAgentState> emit,
  ) async {
    // Slower than the other probes by design: the server makes a real one-turn model request,
    // because an installed binary with an expired token would otherwise report as healthy.
    emit(state.copyWith(probing: true, probeMessage: 'Checking…'));
    try {
      final status = await _checkClaudeAgentSdkStatus();
      emit(state.copyWith(
        probing: false,
        probeMessage: status.isAvailable
            ? 'Claude Agent SDK ready (claude ${status.version ?? 'version unknown'}). '
                'Runs bill this token\'s Claude subscription, not the Anthropic API.'
            : status.error ?? 'The Claude Agent SDK is not usable on this server.',
      ));
    } on ApiException {
      emit(state.copyWith(
        probing: false,
        probeMessage: 'Could not check the Claude Agent SDK on this server.',
      ));
    }
  }

  Future<void> _onGooseStatus(GooseCliStatusRequested event, Emitter<AiAgentState> emit) async {
    emit(state.copyWith(probing: true, probeMessage: 'Checking…'));
    try {
      final status = await _checkGooseCliStatus(event.endpoint?.trim());
      emit(state.copyWith(
        probing: false,
        probeMessage: status.isAvailable
            ? 'Goose agent reachable (${status.version ?? 'version unknown'}).'
            : 'Could not reach the Goose serve endpoint: ${status.error ?? 'unknown error'}',
      ));
    } on ApiException {
      emit(state.copyWith(probing: false, probeMessage: 'Could not reach the Goose serve endpoint.'));
    }
  }
}
