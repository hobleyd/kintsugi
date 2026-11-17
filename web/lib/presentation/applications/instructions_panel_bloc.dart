import 'dart:convert';

import 'package:bloc/bloc.dart';
import 'package:crypto/crypto.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../core/text/script_diff.dart';
import '../../data/models/upgrade_path_mapper.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/upgrade_path.dart';
import '../../domain/usecases/upgrade_path_usecases.dart';

/// What a successful signature produced, for a screen that is about to close the panel that made it.
///
/// The Failed Updates screen takes the row — and so this panel — off the table the moment a repair
/// is signed, which destroys the panel's own "Signed. Opened a pull request" line and its link in
/// the same frame they appear. The pull request is the durable record of the review (see
/// `IScriptApprovalPublisher`), so it has to survive the widget that announced it: the screen
/// re-states it at page level instead.
class SignedScriptOutcome extends Equatable {
  const SignedScriptOutcome({
    required this.clearedPatchFailures,
    required this.approvalDescription,
    required this.approvalPullRequestUrl,
  });

  /// How many outstanding failures the server cleared — see [UpgradePathResult.clearedPatchFailures].
  final int clearedPatchFailures;

  /// The same sentence the panel would have shown about publishing the approval.
  final String approvalDescription;

  final String? approvalPullRequestUrl;

  @override
  List<Object?> get props => [clearedPatchFailures, approvalDescription, approvalPullRequestUrl];
}

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

/// "Clear" was pressed: the box is emptied so a replacement can be pasted. [text] is what it held,
/// kept so that "Undo clear" can put it back — emptying a long script by a misclick should cost one
/// click, not the script.
///
/// Only the box is cleared. Nothing is sent: the server keeps the script it has until a save.
final class EditorCleared extends InstructionsPanelEvent {
  const EditorCleared(this.text);

  final String text;

  @override
  List<Object?> get props => [text];
}

final class EditorClearUndone extends InstructionsPanelEvent {
  const EditorClearUndone();
}

/// The reviewer marked one change as read, or took the mark back. [key] is
/// [ScriptChange.key], which is about the change's content, so the mark follows the change.
final class ChangeReviewToggled extends InstructionsPanelEvent {
  const ChangeReviewToggled(this.key);

  final String key;

  @override
  List<Object?> get props => [key];
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
    this.justSigned = false,
    this.signedOutcome,
    this.baselineText,
    this.baselineSigned = false,
    this.changeKeys = const [],
    this.reviewed = const {},
    this.clearedText,
    this.clearedInSync = false,
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

  /// Set for one state only, after a *successful sign* — never after a save.
  ///
  /// The distinction is the whole point: an unsigned script is one no agent will run, so saving a
  /// fix and stopping there leaves the failure entirely live. Only the signature replaces what was
  /// failing, and only then does the Failed Updates screen take its row off the queue.
  final bool justSigned;

  /// Everything that signature produced which outlives this panel — set exactly when [justSigned] is.
  final SignedScriptOutcome? signedOutcome;

  /// What the box held when this row was opened, or when the script was last signed — the script
  /// the "Changes" tab compares against. Null when the row had nothing to show then.
  ///
  /// Kept here because "Send to AI" and a save both overwrite the stored script, and the panel would
  /// otherwise have nothing to say a new version differs *from*.
  final String? baselineText;

  /// Whether [baselineText] was a signed script. Only then is it something a reviewer has already
  /// trusted, and only then does a difference from it have to be read before it is signed.
  final bool baselineSigned;

  /// [ScriptChange.key] for each change between [baselineText] and the stored script. Empty unless
  /// [baselineSigned]: a script nobody had signed has no reviewed version to differ from.
  final List<String> changeKeys;

  /// The changes the reviewer has marked as read.
  final Set<String> reviewed;

  /// What "Clear" emptied the box of, while "Undo clear" can still put it back. Cleared by the next
  /// edit, which makes the old text no longer the thing to restore.
  final String? clearedText;

  /// Whether the box matched the server when it was cleared. Necessary for an undo to say so again,
  /// never sufficient: see [InstructionsPanelBloc._onUndoClear].
  final bool clearedInSync;

  /// How many changes the stored script has that nobody has marked as read.
  int get unreviewedCount => changeKeys.where((key) => !reviewed.contains(key)).length;

  /// Signing is held until every change from the signed script has been read. Signing records that a
  /// person looked at what the fleet will run; for a script that differs from the one already
  /// trusted, the difference is the thing to look at.
  bool get canSign =>
      inSyncWithServer && (result?.isSignable ?? false) && !signing && !saving && unreviewedCount == 0;

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
    bool justSigned = false,
    SignedScriptOutcome? signedOutcome,
    String? baselineText,
    bool? baselineSigned,
    List<String>? changeKeys,
    Set<String>? reviewed,
    String? clearedText,
    bool? clearedInSync,
    bool clearUndo = false,
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
        // An explicit message wins over [clearMessages], which clears only what is *not* being
        // set: the in-flight emits pass both, to drop the previous outcome and announce the new
        // run in one state. Letting the clear win swallowed "Signing..." and "Saving...", so the
        // button dimmed and nothing said why.
        statusMessage: statusMessage ?? (clearMessages ? null : this.statusMessage),
        saveMessage: saveMessage ?? (clearMessages ? null : this.saveMessage),
        signMessage: signMessage ?? (clearMessages ? null : this.signMessage),
        signApprovalUrl: signApprovalUrl ?? (clearMessages ? null : this.signApprovalUrl),
        loadError: loadError ?? this.loadError,
        reloadTable: reloadTable,
        justSigned: justSigned,
        signedOutcome: signedOutcome,
        baselineText: baselineText ?? this.baselineText,
        baselineSigned: baselineSigned ?? this.baselineSigned,
        changeKeys: changeKeys ?? this.changeKeys,
        reviewed: reviewed ?? this.reviewed,
        clearedText: clearUndo ? null : (clearedText ?? this.clearedText),
        clearedInSync: clearUndo ? false : (clearedInSync ?? this.clearedInSync),
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
        justSigned,
        signedOutcome,
        baselineText,
        baselineSigned,
        changeKeys,
        reviewed,
        clearedText,
        clearedInSync,
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
    this.patchFailureId,
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
          clearUndo: true,
        )));
    on<EditorCleared>((event, emit) => emit(state.copyWith(
          editorText: '',
          inSyncWithServer: false,
          signMessage: '',
          clearedText: event.text,
          clearedInSync: state.inSyncWithServer,
        )));
    on<EditorClearUndone>(_onUndoClear);
    on<ChangeReviewToggled>((event, emit) => emit(state.copyWith(
          reviewed: state.reviewed.contains(event.key)
              ? ({...state.reviewed}..remove(event.key))
              : {...state.reviewed, event.key},
        )));
    on<ScriptSaveRequested>(_onSave);
    on<ScriptSignRequested>(_onSign);
  }

  final String applicationName;

  /// A reported patch failure to repair, when this panel was opened from the Failed Updates screen
  /// rather than from Applications.
  ///
  /// It only ever reaches the prompt route, which composes the repair brief — the failure's output
  /// and the current script — server-side and returns it appended to the ordinary research prompt.
  /// Everything after that is the panel's existing flow unchanged: the operator reads or edits the
  /// text, "Send to AI" is the same refresh with the same prompt override, and the result still
  /// arrives unsigned. Composing that text here instead would put a second author of an AI prompt
  /// in the client, free to drift from `AiUpgradePathResearchClient`.
  final String? patchFailureId;

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
        patchFailureId: patchFailureId,
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
        // What the Changes tab compares against from here on. Not set when there was nothing to show.
        baselineText: prompt.existingResult == null ? null : _editorTextFor(prompt.existingResult),
        baselineSigned: prompt.existingResult?.scriptSigned ?? false,
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
        changeKeys: _changeKeys(matched),
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
        changeKeys: _changeKeys(saved),
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

    // The hash of what the box shows — not of [state.result] — because that is what the reviewer
    // read. The server signs only if what it holds still hashes to this, so any way the two can come
    // apart (another administrator's save, an AI run, a state bug like the old "Undo clear" one)
    // fails closed as a 409 instead of signing a script nobody here looked at.
    final reviewedSha256 = scriptSha256(state.editorText);

    emit(state.copyWith(signing: true, clearMessages: true, signMessage: 'Signing...'));

    try {
      final signed = await _signScript(
        applicationName: applicationName,
        platform: signPlatform,
        scriptSha256: reviewedSha256,
        // Only set when this panel was opened from the Failed Updates screen, which is what makes
        // the signature a repair rather than an ordinary review — see [patchFailureId].
        patchFailureId: patchFailureId,
      );
      emit(state.copyWith(
        signing: false,
        result: signed,
        editorText: _editorTextFor(signed),
        inSyncWithServer: true,
        // What was just signed is the script the next change is read against.
        baselineText: _editorTextFor(signed),
        baselineSigned: true,
        changeKeys: const [],
        reviewed: const {},
        // Signing always stores the local signature regardless of what happens upstream, so this
        // only ever explains the publishing half and never contradicts "signed".
        signMessage: 'Signed. ${_describeApproval(signed)}',
        signApprovalUrl: signed.approvalPullRequestUrl,
        reloadTable: true,
        justSigned: true,
        signedOutcome: SignedScriptOutcome(
          clearedPatchFailures: signed.clearedPatchFailures,
          approvalDescription: _describeApproval(signed),
          approvalPullRequestUrl: signed.approvalPullRequestUrl,
        ),
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(
        signing: false,
        // A 409 here is the reviewed-bytes check refusing: the script was replaced after this panel
        // showed it. The box can no longer be trusted to say what is stored, so Sign stays off until
        // the row is reopened, and the message says why rather than "Failed: conflict".
        inSyncWithServer: error.statusCode == 409 ? false : null,
        signMessage: error.statusCode == 409
            ? 'Not signed: the script on the server is no longer the one shown here - it was changed '
                'after you reviewed it (another save, or an AI run). Close and reopen this row to review '
                'the current script.'
            : 'Failed: ${error.message}',
      ));
    }
  }

  Future<void> _onUndoClear(EditorClearUndone event, Emitter<InstructionsPanelState> emit) async {
    final text = state.clearedText;
    if (text == null) return;

    // In step again only if it was when cleared *and* the restored text is still what this panel
    // last knew to be stored. Remembering the old flag alone was wrong: Clear → Send to AI → Undo put
    // script A back in the box while the server held the AI's script B, with Sign enabled — and Sign
    // would have signed B under a review of A.
    final stillStored = state.result != null && _editorTextFor(state.result) == text;
    emit(state.copyWith(
      editorText: text,
      inSyncWithServer: state.clearedInSync && stillStored,
      clearUndo: true,
    ));
  }

  /// The changes between the script this panel opened with and [stored], as the keys a review is
  /// remembered against. Empty when the opening script was not a signed one: there is then no
  /// trusted version to differ from, and nothing a reviewer has to read before signing.
  List<String> _changeKeys(UpgradePathResult? stored) {
    final baseline = state.baselineText;
    if (!state.baselineSigned || baseline == null || stored == null) return const [];
    return [for (final change in diffScripts(baseline, _editorTextFor(stored)).changes) change.key];
  }

  /// Lowercase hex SHA-256 of [text]'s UTF-8 bytes — the same bytes, and the same encoding, as the
  /// server's `ScriptContentHash.Of`, which is what the sign request is checked against.
  static String scriptSha256(String text) => sha256.convert(utf8.encode(text)).toString();

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
