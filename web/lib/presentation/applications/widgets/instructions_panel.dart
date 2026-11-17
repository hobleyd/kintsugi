import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../core/di/locator.dart';
import '../../../core/platform/page_navigator.dart';
import '../../../core/text/invisible_characters.dart';
import '../../../core/text/script_diff.dart';
import '../../../core/theme/app_theme.dart';
import '../../../core/theme/kintsugi_palette.dart';
import '../../../core/widgets/buttons.dart';
import '../../../core/widgets/status_chip.dart';
import '../../../core/widgets/text_bits.dart';
import '../../../domain/usecases/upgrade_path_usecases.dart';
import '../instructions_panel_bloc.dart';
import 'script_diff_view.dart';

/// The panel that opens under a row: AI instructions on the left, the resulting script on the
/// right, and the save and sign that follow.
///
/// This is where a script goes from generated to executable, so two rules are enforced here rather
/// than left to care. Signing signs what the server already holds, never what is on screen — so it
/// is disabled the moment the editor is touched, and re-enabled only by a save or a fresh AI
/// result. And a newly generated script is always unsigned, so it reaches no host until someone
/// has read it.
class InstructionsPanel extends StatelessWidget {
  const InstructionsPanel({
    super.key,
    required this.applicationName,
    required this.platform,
    required this.onServerStateChanged,
    this.patchFailureId,
    this.onScriptSigned,
  });

  final String applicationName;
  final String platform;

  /// Set by the Failed Updates screen, so the instructions box opens already carrying the repair
  /// brief — the failure's own output and the current script — instead of the plain research
  /// prompt. Composed server-side; see [InstructionsPanelBloc.patchFailureId].
  final String? patchFailureId;

  /// Called after a *successful sign*, with everything that signature produced.
  ///
  /// Separate from [onServerStateChanged], which fires for a save too: only the signature replaces
  /// the script that was failing, so only the signature takes a row off the Failed Updates queue.
  /// Null on the Applications screen, which has no queue to take anything off — and which needs no
  /// hand-off either, since nothing there closes the panel out from under its own messages.
  final void Function(SignedScriptOutcome outcome)? onScriptSigned;

  /// Called after a save or a sign, so the table above re-reads and the status column stops
  /// saying "review and sign".
  final VoidCallback onServerStateChanged;

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => InstructionsPanelBloc(
          applicationName: applicationName,
          platform: platform,
          patchFailureId: patchFailureId,
          getPrompt: locator<GetUpgradePathPrompt>(),
          startRefresh: locator<StartUpgradePathRefresh>(),
          refreshStatus: locator<GetUpgradePathRefreshStatus>(),
          saveUpgradePath: locator<SaveUpgradePath>(),
          signScript: locator<SignUpgradePathScript>(),
        )..add(const PanelOpened()),
        child: _PanelBody(
          onServerStateChanged: onServerStateChanged,
          onScriptSigned: onScriptSigned,
        ),
      );
}

class _PanelBody extends StatefulWidget {
  const _PanelBody({required this.onServerStateChanged, this.onScriptSigned});

  final VoidCallback onServerStateChanged;
  final void Function(SignedScriptOutcome outcome)? onScriptSigned;

  @override
  State<_PanelBody> createState() => _PanelBodyState();
}

class _PanelBodyState extends State<_PanelBody> {
  final _instructions = TextEditingController();
  final _editor = TextEditingController();

  /// The last `editorText` the state handed to the box. The box is rewritten from the state only when
  /// that changes — never merely because the state changed. Typing flips `inSyncWithServer` on the
  /// first keystroke, a new state whose `editorText` is still the stored script, and comparing that
  /// with the box would put the stored script back over what was just typed or pasted.
  String _appliedEditorText = '';

  /// How wide the instructions column is. Draggable, so either box can be made big enough to
  /// actually read what is in it rather than being stuck at half the width.
  double _instructionsWidth = 0;

  @override
  void dispose() {
    _instructions.dispose();
    _editor.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) =>
      BlocConsumer<InstructionsPanelBloc, InstructionsPanelState>(
        listener: (context, state) {
          if (state.prompt != null && _instructions.text.isEmpty) {
            _instructions.text = state.prompt!.prompt ?? '';
          }
          // Assigned from the listener, never from the builder: a builder that wrote to the
          // controller would fight the operator for the cursor, and this box is one somebody types
          // a whole script into.
          if (state.editorText != _appliedEditorText) {
            _appliedEditorText = state.editorText;
            if (_editor.text != state.editorText) _editor.text = state.editorText;
          }

          if (state.reloadTable) widget.onServerStateChanged();
          // After the reload, so the screen acts on a table it has already asked to refresh.
          if (state.signedOutcome case final outcome? when state.justSigned) {
            widget.onScriptSigned?.call(outcome);
          }
        },
        builder: (context, state) {
          if (state.loading) {
            return const Padding(
              padding: EdgeInsets.symmetric(vertical: 16),
              child: HintText('Loading default instructions...'),
            );
          }

          if (state.loadError != null) {
            return HintText('Could not load instructions: ${state.loadError}');
          }

          return LayoutBuilder(
            builder: (context, constraints) {
              const resizerWidth = 10.0;
              const minColumnWidth = 200.0;
              final available = constraints.maxWidth - resizerWidth;
              if (_instructionsWidth == 0) _instructionsWidth = available / 2;
              final left = _instructionsWidth.clamp(minColumnWidth, available - minColumnWidth);

              return Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  SizedBox(width: left, child: _InstructionsColumn(state: state, controller: _instructions)),
                  _Resizer(
                    onDrag: (delta) => setState(
                      () => _instructionsWidth =
                          (left + delta).clamp(minColumnWidth, available - minColumnWidth),
                    ),
                  ),
                  Expanded(child: _ScriptColumn(state: state, controller: _editor)),
                ],
              );
            },
          );
        },
      );
}

class _InstructionsColumn extends StatelessWidget {
  const _InstructionsColumn({required this.state, required this.controller});

  final InstructionsPanelState state;
  final TextEditingController controller;

  @override
  Widget build(BuildContext context) {
    final prompt = state.prompt!;
    final platformNote = prompt.platform == null ? '' : ' (platform: ${prompt.platform})';

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          'AI INSTRUCTIONS$platformNote'.toUpperCase(),
          style: Theme.of(context).textTheme.labelLarge,
        ),
        const SizedBox(height: 6),
        if (!prompt.available)
          // Not available is the ordinary case for a package-manager-managed application: its
          // script is fixed and deterministic, and no AI call is involved in producing it.
          HintText(prompt.reason ?? 'No AI instructions apply to this application.')
        else ...[
          TextField(
            controller: controller,
            enabled: !state.sending,
            maxLines: 14,
            minLines: 14,
            style: AppTheme.mono(color: context.palette.text, size: 13),
          ),
          const SizedBox(height: 10),
          Row(
            children: [
              SecondaryButton(
                label: 'Send to AI',
                busy: state.sending,
                onPressed: () =>
                    context.read<InstructionsPanelBloc>().add(InstructionsSent(controller.text)),
              ),
              if (state.statusMessage != null) ...[
                const SizedBox(width: 12),
                Expanded(child: HintText(state.statusMessage!)),
              ],
            ],
          ),
        ],
      ],
    );
  }
}

enum _ScriptTab { edit, changes }

/// The script on the right of the panel: the box it is edited in, or what has changed in it, with the
/// buttons that save and sign it.
///
/// **Clear and Copy act on the box, and on nothing else.** Clear empties it so a replacement can be
/// pasted; the server keeps the script it has until a save, and "Undo clear" is there for a misclick.
/// Copy puts the whole box on the clipboard.
class _ScriptColumn extends StatefulWidget {
  const _ScriptColumn({required this.state, required this.controller});

  final InstructionsPanelState state;
  final TextEditingController controller;

  @override
  State<_ScriptColumn> createState() => _ScriptColumnState();
}

class _ScriptColumnState extends State<_ScriptColumn> {
  final _focus = FocusNode();
  var _tab = _ScriptTab.edit;

  /// What Copy last did, shown beside the buttons for a moment.
  String? _notice;
  Timer? _noticeTimer;

  // The comparison is recomputed only when the text or the script it is read against changes: a
  // rebuild for an unrelated reason (a message, a hover) should not diff the whole script again.
  String? _diffBaseline;
  String? _diffText;
  ScriptDiff? _diff;

  @override
  void dispose() {
    _focus.dispose();
    _noticeTimer?.cancel();
    super.dispose();
  }

  @override
  void didUpdateWidget(_ScriptColumn old) {
    super.didUpdateWidget(old);
    // A script the AI has just written, which differs from the signed one, is the thing to read: open
    // on the changes rather than on a box of text that looks like what was there before.
    if (old.state.sending && !widget.state.sending && widget.state.changeKeys.isNotEmpty) {
      _tab = _ScriptTab.changes;
    }
  }

  ScriptDiff? _diffFor(String text) {
    final baseline = widget.state.baselineText;
    if (baseline == null) return null;
    if (_diff == null || _diffBaseline != baseline || _diffText != text) {
      _diffBaseline = baseline;
      _diffText = text;
      _diff = diffScripts(baseline, text);
    }
    return _diff;
  }

  void _say(String message) {
    _noticeTimer?.cancel();
    setState(() => _notice = message);
    _noticeTimer = Timer(const Duration(seconds: 2), () {
      if (mounted) setState(() => _notice = null);
    });
  }

  Future<void> _copy() async {
    final text = widget.controller.text;
    if (text.isEmpty) {
      _say('Nothing to copy: the box is empty.');
      return;
    }
    await Clipboard.setData(ClipboardData(text: text));
    if (mounted) _say('Copied the whole script (${text.split('\n').length} lines).');
  }

  void _clear() {
    final controller = widget.controller;
    if (controller.text.isNotEmpty) {
      context.read<InstructionsPanelBloc>().add(EditorCleared(controller.text));
      // Cleared here as well as through the state: the box is only rewritten from the state when it
      // *changes*, and a state that already says the editor is empty would leave the typed text.
      controller.clear();
    }
    // To the box, with the cursor in it, so Ctrl+V is the next thing that happens.
    setState(() => _tab = _ScriptTab.edit);
    _focus.requestFocus();
  }

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<InstructionsPanelBloc>();
    final state = widget.state;
    final controller = widget.controller;

    // Rebuilt as the box is typed in: the changes count, and whether Save has anything to save.
    return ListenableBuilder(
      listenable: controller,
      builder: (context, _) {
        final text = controller.text;
        final diff = _diffFor(text);
        // Of the text Sign would vouch for — the state's, which is what is hashed and sent — not of
        // the box mid-edit, when Sign is off anyway.
        final invisible = findInvisible(state.editorText);
        final blank = text.trim().isEmpty;
        final showChanges = _tab == _ScriptTab.changes && state.baselineText != null;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            _header(context, diff),
            const SizedBox(height: 6),
            if (_tab == _ScriptTab.changes) ..._changesHeader(context),
            if (showChanges)
              _changes(context, diff!, text)
            else if (_tab == _ScriptTab.changes)
              const HintText('Nothing to compare with yet: this row had no script when it was opened.')
            else
              TextField(
                controller: controller,
                focusNode: _focus,
                maxLines: 14,
                minLines: 14,
                style: AppTheme.mono(color: context.palette.text, size: 13),
                decoration: const InputDecoration(
                  hintText: 'Paste a result JSON here to save it directly, without going through the AI '
                      '- or, for a script-based result, just paste a replacement script.',
                ),
                onChanged: (_) => bloc.add(const EditorTouched()),
              ),
            const SizedBox(height: 10),
            Wrap(
              spacing: 12,
              runSpacing: 8,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                SecondaryButton(
                  label: 'Save Script',
                  busy: state.saving,
                  // Nothing to save in an empty box. Saving one would store a script with no body.
                  onPressed: blank ? null : () => bloc.add(ScriptSaveRequested(controller.text)),
                  tooltip: blank ? 'The box is empty. Paste a script, or Undo clear.' : null,
                ),
                if (state.saveMessage != null) HintText(state.saveMessage!),
                SecondaryButton(
                  label: 'Sign Script',
                  busy: state.signing,
                  tooltip: _signTooltip(state),
                  onPressed: state.canSign ? () => _sign(invisible) : null,
                ),
                if (state.signMessage != null && state.signMessage!.isNotEmpty) HintText(state.signMessage!),
                if (invisible.isNotEmpty)
                  Text(
                    key: const ValueKey('script-invisible-warning'),
                    '⚠ ${_describeInvisible(invisible)} The Changes tab shows each one where it is.',
                    style: AppTheme.mono(color: context.palette.amber, size: 12),
                  ),
                if (_awaitingReview(state))
                  HintText(
                    '${state.unreviewedCount} change${state.unreviewedCount == 1 ? '' : 's'} left to read '
                    'before signing — see the Changes tab.',
                  ),
                if (state.signApprovalUrl != null)
                  LinkText(
                    label: 'View pull request',
                    onTap: () => locator<PageNavigator>().go(state.signApprovalUrl!),
                  ),
              ],
            ),
          ],
        );
      },
    );
  }

  /// "UPDATE SCRIPT", the Edit / Changes tabs, and the small buttons that act on the box.
  Widget _header(BuildContext context, ScriptDiff? diff) {
    final bloc = context.read<InstructionsPanelBloc>();
    final state = widget.state;

    return Wrap(
      spacing: 10,
      runSpacing: 6,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        Text('UPDATE SCRIPT', style: Theme.of(context).textTheme.labelLarge),
        _Tabs(
          tab: _tab,
          changes: diff?.changes.length,
          onTab: (tab) => setState(() => _tab = tab),
        ),
        _MiniButton(
          key: const ValueKey('script-copy'),
          icon: Icons.copy_outlined,
          label: 'Copy',
          tooltip: 'Copy the whole script',
          onPressed: _copy,
        ),
        _MiniButton(
          key: const ValueKey('script-clear'),
          icon: Icons.delete_outline,
          label: 'Clear',
          tooltip: 'Empty this box so you can paste a new script. Nothing changes on the server until you press Save Script.',
          danger: true,
          onPressed: _clear,
        ),
        if (state.clearedText != null)
          LinkText(
            key: const ValueKey('script-undo-clear'),
            label: 'Undo clear',
            onTap: () {
              bloc.add(const EditorClearUndone());
              _focus.requestFocus();
            },
          ),
        if (_notice != null) HintText(_notice!),
      ],
    );
  }

  /// What the Changes tab is comparing: the script as the row opened with it, and what is in the box.
  List<Widget> _changesHeader(BuildContext context) {
    final state = widget.state;
    if (state.baselineText == null) return const [];

    final String now;
    final String nowKey;
    if (widget.controller.text.trim().isEmpty) {
      now = 'Now · box is empty';
      nowKey = 'offline';
    } else if (!state.inSyncWithServer) {
      now = 'Now · unsaved edits';
      nowKey = 'offline';
    } else if (state.result?.scriptSigned ?? false) {
      now = 'Now · signed';
      nowKey = 'online';
    } else {
      now = 'Now · saved, unsigned';
      nowKey = 'review-sign';
    }

    return [
      Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: Wrap(
          spacing: 10,
          runSpacing: 6,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            StatusChip(
              state.baselineSigned ? 'As opened · signed' : 'As opened · not signed',
              statusKey: state.baselineSigned ? 'online' : '_accent',
            ),
            Text('→', style: TextStyle(color: context.palette.muted)),
            StatusChip(now, statusKey: nowKey),
          ],
        ),
      ),
    ];
  }

  Widget _changes(BuildContext context, ScriptDiff diff, String text) {
    final state = widget.state;
    final bloc = context.read<InstructionsPanelBloc>();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (text.trim().isEmpty)
          const Padding(
            padding: EdgeInsets.only(bottom: 8),
            child: HintText(
              'The box is empty. Paste the new script on the Edit tab; this tab then shows what is '
              'different from the script you opened.',
            ),
          ),
        if (diff.isEmpty)
          const HintText('No changes: this is the same script that was here when you opened this row.')
        else
          ScriptDiffView(
            diff: diff,
            reviewed: state.reviewed,
            // Only a script that differs from a *signed* one has to be read before it is signed.
            reviewRequired: state.baselineSigned,
            onToggleReviewed: (key) => bloc.add(ChangeReviewToggled(key)),
          ),
      ],
    );
  }

  /// Signs, after one more question when the script holds characters the reviewer cannot see.
  ///
  /// The Changes tab draws them, but only on lines that changed, and the Edit box draws them as
  /// nothing at all. A right-to-left override or a zero-width character in a script is almost never
  /// legitimate and is exactly how a line is made to read differently from how it runs, so signing
  /// one is a decision to make on purpose rather than by not noticing.
  Future<void> _sign(List<InvisibleCharacter> invisible) async {
    final bloc = context.read<InstructionsPanelBloc>();
    if (invisible.isEmpty) {
      bloc.add(const ScriptSignRequested());
      return;
    }

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: dialogContext.palette.backgroundAlt,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(6),
          side: BorderSide(color: dialogContext.palette.border),
        ),
        title: Text('Sign a script with hidden characters?', style: Theme.of(dialogContext).textTheme.titleLarge),
        content: HintText(
          '${_describeInvisible(invisible)}\n\n'
          'These do not show in the editor. A text-direction character can make a line display in a '
          'different order from the one the shell runs it in, and a zero-width character or a stray '
          'carriage return can make two different lines look the same. Sign only if you know why each '
          'one is there.',
        ),
        actions: [
          SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(dialogContext).pop(false)),
          PrimaryButton(label: 'Sign anyway', onPressed: () => Navigator.of(dialogContext).pop(true)),
        ],
      ),
    );
    if (confirmed == true && mounted) bloc.add(const ScriptSignRequested());
  }

  /// "This script contains 2 hidden characters: U+202E right-to-left override (line 4), …"
  static String _describeInvisible(List<InvisibleCharacter> invisible) {
    final byCode = <String, List<InvisibleCharacter>>{};
    for (final c in invisible) {
      (byCode['${c.code} ${c.name}'] ??= []).add(c);
    }
    final parts = [
      for (final MapEntry(:key, :value) in byCode.entries)
        '$key (line${value.length == 1 ? '' : 's'} '
            '${value.take(5).map((c) => c.line).toSet().join(', ')}${value.length > 5 ? ', …' : ''})',
    ];
    return 'This script contains ${invisible.length} hidden character${invisible.length == 1 ? '' : 's'}: '
        '${parts.join('; ')}.';
  }

  bool _awaitingReview(InstructionsPanelState state) =>
      state.unreviewedCount > 0 && state.inSyncWithServer && (state.result?.isSignable ?? false);

  String _signTooltip(InstructionsPanelState state) {
    if (state.signing) return 'Signing the saved script';
    if (state.canSign) return 'Sign the saved script so an agent will run it';
    if (_awaitingReview(state)) {
      return 'This script differs from the signed one. Open the Changes tab and mark each change as '
          'reviewed first.';
    }
    return 'Signing signs what the server already holds. Save your changes first, or run '
        'the AI again, to bring this box back in step with it.';
  }
}

/// "Edit | Changes (3)". Changes is dimmed, and says why, for a row that had no script to compare.
class _Tabs extends StatelessWidget {
  const _Tabs({required this.tab, required this.changes, required this.onTab});

  final _ScriptTab tab;

  /// How many changes there are, or null when there is no script to compare against.
  final int? changes;
  final ValueChanged<_ScriptTab> onTab;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    Widget one(_ScriptTab value, String label, {int? count, bool enabled = true, String? tooltip}) {
      final on = tab == value;
      final content = InkWell(
        key: ValueKey('script-tab-${value.name}'),
        onTap: enabled ? () => onTab(value) : null,
        child: Semantics(
          button: true,
          selected: on,
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 5),
            color: on ? palette.neon.withValues(alpha: 0.16) : null,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  label.toUpperCase(),
                  style: AppTheme.display(
                    color: !enabled ? palette.muted.withValues(alpha: 0.5) : (on ? palette.neon : palette.muted),
                    size: 9.5,
                    letterSpacing: 0.8,
                  ),
                ),
                if (count != null && count > 0) ...[
                  const SizedBox(width: 6),
                  Text('$count', style: AppTheme.display(color: palette.amber, size: 9.5, letterSpacing: 0.5)),
                ],
              ],
            ),
          ),
        ),
      );
      return tooltip == null ? content : Tooltip(message: tooltip, child: content);
    }

    return Container(
      decoration: BoxDecoration(
        border: Border.all(color: palette.border),
        borderRadius: BorderRadius.circular(3),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          one(_ScriptTab.edit, 'Edit'),
          one(
            _ScriptTab.changes,
            'Changes',
            count: changes,
            enabled: changes != null,
            tooltip: changes == null ? 'Nothing to compare with yet: this row had no script when it was opened.' : null,
          ),
        ],
      ),
    );
  }
}

/// A small outlined button for acting on the box: [danger] turns it red under the pointer.
class _MiniButton extends StatefulWidget {
  const _MiniButton({
    super.key,
    required this.icon,
    required this.label,
    required this.tooltip,
    required this.onPressed,
    this.danger = false,
  });

  final IconData icon;
  final String label;
  final String tooltip;
  final VoidCallback onPressed;
  final bool danger;

  @override
  State<_MiniButton> createState() => _MiniButtonState();
}

class _MiniButtonState extends State<_MiniButton> {
  bool _hovering = false;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final accent = widget.danger ? palette.red : palette.neon;
    final color = _hovering ? accent : palette.neonDim;

    return Tooltip(
      message: widget.tooltip,
      child: MouseRegion(
        onEnter: (_) => setState(() => _hovering = true),
        onExit: (_) => setState(() => _hovering = false),
        child: InkWell(
          onTap: widget.onPressed,
          borderRadius: BorderRadius.circular(3),
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 5),
            decoration: BoxDecoration(
              border: Border.all(color: _hovering ? accent : palette.border),
              borderRadius: BorderRadius.circular(3),
              boxShadow: _hovering && palette.glowsEnabled
                  ? [BoxShadow(color: accent.withValues(alpha: 0.35), blurRadius: 10)]
                  : const [],
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(widget.icon, size: 13, color: color),
                const SizedBox(width: 5),
                Text(widget.label.toUpperCase(), style: AppTheme.display(color: color, size: 9.5, letterSpacing: 0.8)),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The draggable divider between the two columns.
class _Resizer extends StatelessWidget {
  const _Resizer({required this.onDrag});

  final ValueChanged<double> onDrag;

  @override
  Widget build(BuildContext context) => MouseRegion(
        cursor: SystemMouseCursors.resizeLeftRight,
        child: GestureDetector(
          // Opaque, because the default defers to the child and the only painted child is the
          // 1px line: a mouse-down anywhere else in the 10px gutter hit nothing, so the drag was
          // accepted only on that exact pixel column. The MouseRegion above is opaque over the
          // whole gutter, so the cursor said "resize" across nine pixels that did nothing.
          behavior: HitTestBehavior.opaque,
          onHorizontalDragUpdate: (details) => onDrag(details.delta.dx),
          child: Container(
            width: 10,
            height: 320,
            alignment: Alignment.center,
            child: Container(width: 1, color: context.palette.border),
          ),
        ),
      );
}
