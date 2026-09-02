import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../core/di/injection.dart';
import '../../../core/platform/page_navigator.dart';
import '../../../core/theme/app_theme.dart';
import '../../../core/theme/kintsugi_palette.dart';
import '../../../core/widgets/buttons.dart';
import '../../../core/widgets/text_bits.dart';
import '../../../domain/usecases/upgrade_path_usecases.dart';
import '../instructions_panel_bloc.dart';

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
  });

  final String applicationName;
  final String platform;

  /// Called after a save or a sign, so the table above re-reads and the status column stops
  /// saying "review and sign".
  final VoidCallback onServerStateChanged;

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => InstructionsPanelBloc(
          applicationName: applicationName,
          platform: platform,
          getPrompt: locator<GetUpgradePathPrompt>(),
          startRefresh: locator<StartUpgradePathRefresh>(),
          refreshStatus: locator<GetUpgradePathRefreshStatus>(),
          saveUpgradePath: locator<SaveUpgradePath>(),
          signScript: locator<SignUpgradePathScript>(),
        )..add(const PanelOpened()),
        child: _PanelBody(onServerStateChanged: onServerStateChanged),
      );
}

class _PanelBody extends StatefulWidget {
  const _PanelBody({required this.onServerStateChanged});

  final VoidCallback onServerStateChanged;

  @override
  State<_PanelBody> createState() => _PanelBodyState();
}

class _PanelBodyState extends State<_PanelBody> {
  final _instructions = TextEditingController();
  final _editor = TextEditingController();

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
          if (state.editorText != _editor.text) _editor.text = state.editorText;

          if (state.reloadTable) widget.onServerStateChanged();
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
                onPressed: state.sending
                    ? null
                    : () => context
                        .read<InstructionsPanelBloc>()
                        .add(InstructionsSent(controller.text)),
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

class _ScriptColumn extends StatelessWidget {
  const _ScriptColumn({required this.state, required this.controller});

  final InstructionsPanelState state;
  final TextEditingController controller;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<InstructionsPanelBloc>();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text('UPDATE SCRIPT', style: Theme.of(context).textTheme.labelLarge),
        const SizedBox(height: 6),
        TextField(
          controller: controller,
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
              onPressed: state.saving ? null : () => bloc.add(ScriptSaveRequested(controller.text)),
            ),
            if (state.saveMessage != null) HintText(state.saveMessage!),
            SecondaryButton(
              label: 'Sign Script',
              tooltip: state.canSign
                  ? 'Sign the saved script so an agent will run it'
                  : 'Signing signs what the server already holds. Save your changes first, or run '
                      'the AI again, to bring this box back in step with it.',
              onPressed: state.canSign ? () => bloc.add(const ScriptSignRequested()) : null,
            ),
            if (state.signMessage != null && state.signMessage!.isNotEmpty)
              HintText(state.signMessage!),
            if (state.signApprovalUrl != null)
              LinkText(
                label: 'View pull request',
                onTap: () => locator<PageNavigator>().go(state.signApprovalUrl!),
              ),
          ],
        ),
      ],
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
