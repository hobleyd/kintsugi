import 'package:flutter/material.dart';

import '../../../core/di/locator.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/widgets/alert_box.dart';
import '../../../core/widgets/buttons.dart';
import '../../../core/widgets/text_bits.dart';
import '../../../domain/usecases/managed_application_usecases.dart';
import 'managed_dialog.dart';

/// What a row opens to when another tool patches the application: who, and the two things that can be
/// done about it. It stands where the AI instructions would be, because for this row there is nothing
/// for the AI to do — which is the point of marking it.
class ManagedRowPanel extends StatefulWidget {
  const ManagedRowPanel({
    super.key,
    required this.applicationName,
    required this.owner,
    required this.onChanged,
  });

  final String applicationName;
  final String owner;

  /// Called after a change, so the table re-reads and the row shows what is now true.
  final VoidCallback onChanged;

  @override
  State<ManagedRowPanel> createState() => _ManagedRowPanelState();
}

class _ManagedRowPanelState extends State<ManagedRowPanel> {
  bool _working = false;
  String? _error;

  Future<void> _change() async {
    final saved = await showManagedDialog(
      context,
      applicationName: widget.applicationName,
      currentOwner: widget.owner,
    );
    if (saved && mounted) widget.onChanged();
  }

  Future<void> _release() async {
    setState(() {
      _working = true;
      _error = null;
    });
    try {
      await locator<UnmarkApplicationManaged>()(widget.applicationName);
      if (mounted) widget.onChanged();
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _working = false;
          _error = error.message;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Managed by ${widget.owner}.',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.w600),
            ),
            const SizedBox(height: 4),
            const HintText(
              'Kintsugi does not research or patch this application, and does not count it as waiting '
              'for an update. A script it already has is kept, and no agent runs it.',
            ),
            const SizedBox(height: 12),
            if (_error != null) ...[AlertBox.error(_error!), const SizedBox(height: 10)],
            Wrap(
              spacing: 12,
              runSpacing: 8,
              children: [
                SecondaryButton(label: 'Change owner', onPressed: _working ? null : _change),
                SecondaryButton(label: 'Stop treating as managed', busy: _working, onPressed: _release),
              ],
            ),
          ],
        ),
      );
}
