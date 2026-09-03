import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../../core/theme/app_theme.dart';
import '../../../core/theme/kintsugi_palette.dart';
import '../../../core/widgets/buttons.dart';

/// Shows one row's script, read-only, with a copy button.
///
/// Read-only deliberately: editing a script happens in the row's own panel, where the save and the
/// signature that follow are in view. A dialog that let a script be edited with no way to save it
/// would invite exactly the mistake of thinking a change had taken effect.
Future<void> showScriptDialog(
  BuildContext context, {
  required String applicationName,
  required String platform,
  required String script,
}) =>
    showDialog<void>(
      context: context,
      builder: (dialogContext) => _ScriptDialog(
        title: platform.isEmpty ? applicationName : '$applicationName ($platform)',
        script: script,
      ),
    );

class _ScriptDialog extends StatefulWidget {
  const _ScriptDialog({required this.title, required this.script});

  final String title;
  final String script;

  @override
  State<_ScriptDialog> createState() => _ScriptDialogState();
}

class _ScriptDialogState extends State<_ScriptDialog> {
  String? _copyStatus;

  Future<void> _copy() async {
    try {
      await Clipboard.setData(ClipboardData(text: widget.script));
      setState(() => _copyStatus = 'Copied.');
    } on Object {
      // The clipboard is unavailable outside a secure context, which is the plain-HTTP LAN case.
      setState(() => _copyStatus = 'Could not copy - select the text and copy manually.');
    }
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Dialog(
      backgroundColor: palette.backgroundAlt,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(6),
        side: BorderSide(color: palette.border),
      ),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 900, maxHeight: 700),
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(widget.title, style: Theme.of(context).textTheme.titleLarge),
                  ),
                  IconActionButton(
                    icon: Icons.close,
                    tooltip: 'Close',
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
              const SizedBox(height: 16),
              Expanded(
                child: Container(
                  padding: const EdgeInsets.all(14),
                  decoration: BoxDecoration(
                    color: palette.accentWash(0.04),
                    border: Border.all(color: palette.border),
                    borderRadius: BorderRadius.circular(3),
                  ),
                  child: SingleChildScrollView(
                    // Text, not SelectableText: the app-wide SelectionArea in main.dart already
                    // makes this selectable, and a SelectableText inside one is a selection
                    // *island* a drag starting outside it stops at — which on this dialog would
                    // exclude the script, the one thing anyone is here to copy.
                    child: Text(
                      widget.script,
                      style: AppTheme.mono(color: palette.text, size: 13),
                    ),
                  ),
                ),
              ),
              const SizedBox(height: 16),
              Row(
                children: [
                  SecondaryButton(label: 'Copy to clipboard', onPressed: _copy),
                  if (_copyStatus != null) ...[
                    const SizedBox(width: 12),
                    Text(_copyStatus!, style: Theme.of(context).textTheme.bodySmall),
                  ],
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}
