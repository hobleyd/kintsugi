import 'dart:convert';

import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../data/models/upgrade_path_mapper.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/upgrade_path.dart';
import '../../domain/usecases/upgrade_path_usecases.dart';

sealed class InstructionsPanelEvent extends Equatable {
  const InstructionsPanelEvent();

  @override
  List<Object?> get props => const [];
}

/// Loads the default AI instructions for this row, and picks up a refresh that is already running.
final class PanelOpened extends InstructionsPanelEvent {
  const PanelOpened();
}

final class InstructionsSent extends InstructionsPanelEvent {
  const InstructionsSent(this.instructions);

  final String instructions;

  @override
  List<Object?> get props => [instructions];
}

final class RefreshStatusPolled extends InstructionsPanelEvent {
  const RefreshStatusPolled();
}

/// The editor was typed in. Signing is disabled from here until a save or a fresh AI result brings
/// the box back into agreement with what is stored.
final class EditorTouched extends InstructionsPanelEvent {
  const EditorTouched();
}

final class ScriptSaveRequested extends InstructionsPanelEvent {
  const ScriptSaveRequested(this.editorText);

  final String editorText;

  @override
  List<Object?> get props => [editorText];
}

final class ScriptSignRequested extends InstructionsPanelEvent {
  const ScriptSignRequested();
}

final class InstructionsPanelState extends Equatable {
  const InstructionsPanelState({
    this.loading = true,
    this.prompt,
    this.result,
    this.editorText = '',
    this.sending = false,
    this.saving = false,
    this.signing = false,
    this.inSyncWithServer = false,
    this.statusMessage,
    this.saveMessage,
    this.signMessage,
    this.signApprovalUrl,
    this.loadError,
    this.reloadTable = false,
  });

  final bool loading;
  final UpgradePathPrompt? prompt;

  /// The result currently on screen. Also what a pasted replacement script is merged onto, so the
  /// fields a bare script does not carry — platform, notes, latest version — survive the save.
  final UpgradePathResult? result;

  /// What the editor should contain. For a script result this is just the script, which is the
  /// thing there is to review, rather than that script escaped inside a JSON envelope. Anything
  /// else keeps the full JSON, since no single field is obviously "the" content.
  final String editorText;

  final bool sending;
  final bool saving;
  final bool signing;

  /// Whether [editorText] still matches what the server holds.
  ///
  /// This is what gates signing, and it is the panel's most important rule: signing signs whatever
  /// is *already stored*, never what is on screen. A signature over text the operator had just
  /// edited would look like a review of content the fleet is not going to run.
  final bool inSyncWithServer;

  final String? statusMessage;
  final String? saveMessage;
  final String? signMessage;
  final String? signApprovalUrl;
  final String? loadError;

  /// Set for one state only, after a save or a sign, so the screen re-reads the table.
  final bool reloadTable;

  bool get canSign => inSyncWithServer && (result?.isSignable ?? false) && !signing && !saving;

  InstructionsPanelState copyWith({
    bool? loading,
    UpgradePathPrompt? prompt,
    UpgradePathResult? result,
    String? editorText,
    bool? sending,
    bool? saving,
    bool? signing,
    bool? inSyncWithServer,
    String? statusMessage,
    String? saveMessage,
    String? signMessage,
    String? signApprovalUrl,
    String? loadError,
    bool reloadTable = false,
    bool clearResult = false,
    bool clearMessages = false,
  }) =>
      InstructionsPanelState(
        loading: loading ?? this.loading,
        prompt: prompt ?? this.prompt,
        result: clearResult ? null : (result ?? this.result),
        editorText: editorText ?? this.editorText,
        sending: sending ?? this.sending,
        saving: saving ?? this.saving,
        signing: signing ?? this.signing,
        inSyncWithServer: inSyncWithServer ?? this.inSyncWithServer,
        statusMessage: clearMessages ? null : (statusMessage ?? this.statusMessage),
        saveMessage: clearMessages ? null : (saveMessage ?? this.saveMessage),
        signMessage: clearMessages ? null : (signMessage ?? this.signMessage),
        signApprovalUrl: clearMessages ? null : (signApprovalUrl ?? this.signApprovalUrl),
        loadError: loadError ?? this.loadError,
        reloadTable: reloadTable,
      );

  @override
  List<Object?> get props => [
        loading,
        prompt,
        result,
        editorText,
        sending,
        saving,
        signing,
        inSyncWithServer,
        statusMessage,
        saveMessage,
        signMessage,
        signApprovalUrl,
        loadError,
        reloadTable,
      ];
}

/// One expanded row's AI panel: the instructions, the resulting script, and the save and sign that
/// follow.
///
/// One instance per open panel, created when the row expands and closed when it collapses, so the
/// polling it does while a refresh runs cannot outlive the panel.
class InstructionsPanelBloc extends Bloc<InstructionsPanelEvent, InstructionsPanelState>
    with Polling<InstructionsPanelEvent, InstructionsPanelState> {
  InstructionsPanelBloc({
    required this.applicationName,
    required this.platform,
    required GetUpgradePathPrompt getPrompt,
    required StartUpgradePathRefresh startRefresh,
    required GetUpgradePathRefreshStatus refreshStatus,
    required SaveUpgradePath saveUpgradePath,
    required SignUpgradePathScript signScript,
  })  : _getPrompt = getPrompt,
        _startRefresh = startRefresh,
        _refreshStatus = refreshStatus,
        _saveUpgradePath = saveUpgradePath,
        _signScript = signScript,
        super(const InstructionsPanelState()) {
    on<PanelOpened>(_onOpened);
    on<InstructionsSent>(_onSend);
    on<RefreshStatusPolled>(_onPoll);
    on<EditorTouched>((_, emit) => emit(state.copyWith(
          inSyncWithServer: false,
          signMessage: '',
        )));
    on<ScriptSaveRequested>(_onSave);
    on<ScriptSignRequested>(_onSign);
  }

  final String applicationName;

  /// The row's platform, which may be empty for an application nothing has been researched for.
  /// The prompt response then reports the platform research would use, and that becomes the one
  /// every later call sends.
  final String platform;

  final GetUpgradePathPrompt _getPrompt;
  final StartUpgradePathRefresh _startRefresh;
  final GetUpgradePathRefreshStatus _refreshStatus;
  final SaveUpgradePath _saveUpgradePath;
  final SignUpgradePathScript _signScript;

  String? _resolvedPlatform;

  /// The platform to send. The prompt's answer wins, because for an unresearched application the
  /// row itself has none.
  String? get _platformToSend =>
      (_resolvedPlatform?.isNotEmpty ?? false) ? _resolvedPlatform : (platform.isEmpty ? null : platform);

  Future<void> _onOpened(PanelOpened event, Emitter<InstructionsPanelState> emit) async {
    try {
      final prompt = await _getPrompt(
        applicationName: applicationName,
        platform: platform.isEmpty ? null : platform,
      );
      _resolvedPlatform = prompt.platform ?? (platform.isEmpty ? null : platform);

      emit(state.copyWith(
        loading: false,
        prompt: prompt,
        // An already-successful check shows up by default, rather than leaving the box looking
        // empty until somebody re-runs the AI unnecessarily.
        result: prompt.existingResult,
        editorText: _editorTextFor(prompt.existingResult),
        inSyncWithServer: prompt.existingResult != null,
      ));

      // A "Send to AI" started before this panel existed — the screen was navigated away from and
      // back, or another tab started it — leaves a background job running with nothing here
      // reflecting it. Pick it up rather than looking idle while it works.
      if (prompt.available) add(const RefreshStatusPolled());
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, loadError: error.message));
    }
  }

  Future<void> _onSend(InstructionsSent event, Emitter<InstructionsPanelState> emit) async {
    emit(state.copyWith(
      sending: true,
      clearResult: true,
      editorText: '',
      inSyncWithServer: false,
      clearMessages: true,
      statusMessage: 'Sending to AI...',
    ));

    try {
      final started = await _startRefresh(
        applicationName: applicationName,
        platform: _platformToSend,
        instructions: event.instructions,
      );

      if (!started.started) {
        emit(state.copyWith(
          sending: false,
          statusMessage: 'Already running - this application is already being refreshed.',
        ));
        startPolling(const Duration(seconds: 3), const RefreshStatusPolled());
        return;
      }

      emit(state.copyWith(statusMessage: 'Researching...'));
      startPolling(const Duration(seconds: 3), const RefreshStatusPolled());
    } on ApiException catch (error) {
      emit(state.copyWith(sending: false, statusMessage: 'Failed: ${error.message}'));
    }
  }

  Future<void> _onPoll(RefreshStatusPolled event, Emitter<InstructionsPanelState> emit) async {
    try {
      final status = await _refreshStatus(applicationName);

      if (status.isRunning) {
        final elapsed = status.startedUtc == null
            ? null
            : DateTime.now().difference(status.startedUtc!).inSeconds;
        emit(state.copyWith(
          sending: true,
          statusMessage: elapsed == null ? 'Researching...' : 'Researching... (${elapsed}s)',
        ));
        if (!isPolling) startPolling(const Duration(seconds: 3), const RefreshStatusPolled());
        return;
      }

      stopPolling();

      // Nothing has run for this application. Leave the panel exactly as the prompt left it.
      if (!state.sending) return;

      final matched = status.resultFor(_platformToSend);
      if (matched == null) {
        emit(state.copyWith(
          sending: false,
          statusMessage: 'Failed: ${status.errorMessage ?? 'no result was returned.'}',
        ));
        return;
      }

      // The AI flow persists straight to the database as soon as it finishes, always unsigned, so
      // a freshly generated script is immediately reviewable and signable here with no separate
      // save step in between.
      emit(state.copyWith(
        sending: false,
        statusMessage: status.success == true ? 'Done.' : 'Failed.',
        result: matched,
        editorText: _editorTextFor(matched),
        inSyncWithServer: true,
      ));
    } on ApiException {
      // Transient. The next poll retries; blanking a panel mid-run would be worse.
    }
  }

  Future<void> _onSave(ScriptSaveRequested event, Emitter<InstructionsPanelState> emit) async {
    emit(state.copyWith(saving: true, clearMessages: true, saveMessage: 'Saving...'));

    try {
      final saved = await _saveUpgradePath(_saveBody(event.editorText));
      emit(state.copyWith(
        saving: false,
        saveMessage: 'Saved.',
        result: saved,
        editorText: _editorTextFor(saved),
        inSyncWithServer: true,
        reloadTable: true,
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(saving: false, saveMessage: 'Failed: ${error.message}'));
    }
  }

  Future<void> _onSign(ScriptSignRequested event, Emitter<InstructionsPanelState> emit) async {
    final signPlatform = _platformToSend;
    if (signPlatform == null) {
      emit(state.copyWith(signMessage: 'Failed: this row has no platform to sign against.'));
      return;
    }

    emit(state.copyWith(signing: true, clearMessages: true, signMessage: 'Signing...'));

    try {
      final signed = await _signScript(applicationName: applicationName, platform: signPlatform);
      emit(state.copyWith(
        signing: false,
        result: signed,
        editorText: _editorTextFor(signed),
        inSyncWithServer: true,
        // Signing always stores the local signature regardless of what happens upstream, so this
        // only ever explains the publishing half and never contradicts "signed".
        signMessage: 'Signed. ${_describeApproval(signed)}',
        signApprovalUrl: signed.approvalPullRequestUrl,
        reloadTable: true,
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(signing: false, signMessage: 'Failed: ${error.message}'));
    }
  }

  /// What the editor shows for a result.
  static String _editorTextFor(UpgradePathResult? result) {
    if (result == null) return '';
    if (result.hasScript) return result.script!;
    return const JsonEncoder.withIndent('  ').convert(result.raw);
  }

  /// Builds the save body from whatever is in the editor.
  ///
  /// The box accepts three things, and the order matters. A whole result JSON is sent as-is. An
  /// envelope of the shape a "Send to AI" response comes back as — `{ success, results: [...] }` —
  /// is unwrapped, so a response copied out of this same box can be pasted back in unchanged. And
  /// anything that is not JSON at all is a pasted replacement script: it becomes the script on
  /// whatever result was last shown here, keeping every other already-known field — platform,
  /// notes, latest version — rather than dropping them.
  Map<String, dynamic> _saveBody(String editorText) {
    Map<String, dynamic> body;

    try {
      final decoded = jsonDecode(editorText);
      if (decoded is! Map<String, dynamic>) throw const FormatException();

      body = Map<String, dynamic>.from(decoded);
      final results = body['results'];
      if (results is List && results.isNotEmpty && body['applicationName'] == null) {
        final first = results.first;
        if (first is Map<String, dynamic>) body = Map<String, dynamic>.from(first);
      }
    } on Object {
      body = {
        ...?state.result?.raw,
        'method': upgradeMethodToJson(UpgradeMethod.script),
        'script': editorText,
      };
    }

    // The route requires both of these, and a pasted body may carry neither.
    body['applicationName'] ??= applicationName;
    body['platform'] ??= _platformToSend;

    // A name, never an ordinal: LenientEnumConverter reads UpgradeMethod from a string only.
    final method = body['method'];
    if (method is num) {
      body['method'] = upgradeMethodToJson(
        UpgradeMethod.values[method.toInt().clamp(0, UpgradeMethod.values.length - 1)],
      );
    }

    return body;
  }

  static String _describeApproval(UpgradePathResult result) => switch (result.approvalOutcome) {
        ScriptApprovalPublishOutcome.pullRequestOpened => 'Opened a pull request for review.',
        ScriptApprovalPublishOutcome.pullRequestAlreadyOpen =>
          'Already proposed in an open pull request - nothing new to raise.',
        ScriptApprovalPublishOutcome.alreadyApproved =>
          'This exact script is already approved on the default branch - nothing new to raise.',
        ScriptApprovalPublishOutcome.disabled => 'No pull request raised: '
            '${result.approvalMessage ?? 'no script-approval token is configured.'}',
        ScriptApprovalPublishOutcome.failed =>
          'Publishing to GitHub failed: ${result.approvalMessage ?? 'unknown error.'}',
        _ => '',
      };
}
