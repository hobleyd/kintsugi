import 'package:flutter/material.dart';

import '../../../core/di/locator.dart';
import '../../../core/network/api_exception.dart';
import '../../../core/theme/kintsugi_palette.dart';
import '../../../core/widgets/alert_box.dart';
import '../../../core/widgets/buttons.dart';
import '../../../core/widgets/form_bits.dart';
import '../../../core/widgets/text_bits.dart';
import '../../../domain/usecases/managed_application_usecases.dart';

/// What the owner picker offers for a tool that is not on the list yet.
const managedOtherOwner = 'Other…';

/// "Patched by another tool": asks who patches [applicationName] and, once confirmed, tells the
/// server so. Completes with true when something was saved, so the caller knows to re-read.
///
/// The consequence is stated in the dialog and not left to the tooltip that opened it, because
/// marking is not undoable by walking away from it: Kintsugi stops researching the application and
/// stops patching it, on every host and platform. It is a decision, and this is where it is made.
Future<bool> showManagedDialog(
  BuildContext context, {
  required String applicationName,
  String? currentOwner,
}) async =>
    await showDialog<bool>(
      context: context,
      builder: (_) => _ManagedDialog(applicationName: applicationName, currentOwner: currentOwner),
    ) ??
    false;

class _ManagedDialog extends StatefulWidget {
  const _ManagedDialog({required this.applicationName, required this.currentOwner});

  final String applicationName;
  final String? currentOwner;

  @override
  State<_ManagedDialog> createState() => _ManagedDialogState();
}

class _ManagedDialogState extends State<_ManagedDialog> {
  final _other = TextEditingController();

  List<String>? _owners;
  String? _choice;
  String? _error;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _other.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final state = await locator<GetManagedApplications>()();
      if (!mounted) return;
      setState(() {
        _owners = state.owners;
        final current = widget.currentOwner;
        _choice = current != null && state.owners.contains(current)
            ? current
            : (state.owners.isEmpty ? managedOtherOwner : state.owners.first);
      });
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    }
  }

  String? get _owner => _choice == managedOtherOwner ? _other.text.trim() : _choice;

  Future<void> _save() async {
    final owner = _owner;
    if (owner == null || owner.isEmpty) {
      setState(() => _error = 'Name the tool that patches it.');
      return;
    }

    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await locator<MarkApplicationsManaged>()(names: [widget.applicationName], owner: owner);
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _saving = false;
          _error = error.validationErrors.isEmpty ? error.message : error.validationErrors.values.expand((m) => m).join(' ');
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final owners = _owners;
    final changing = widget.currentOwner != null;

    return AlertDialog(
      backgroundColor: palette.backgroundAlt,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(6),
        side: BorderSide(color: palette.border),
      ),
      title: Text(
        changing ? 'Change who patches it' : 'Patched by another tool',
        style: Theme.of(context).textTheme.titleLarge,
      ),
      content: SizedBox(
        width: 460,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            HintText(widget.applicationName),
            const SizedBox(height: 12),
            if (owners == null && _error == null)
              const HintText('Loading…')
            else if (owners != null) ...[
              LabelledField(
                label: 'Who patches it?',
                child: KintsugiDropdown<String>(
                  value: _choice ?? managedOtherOwner,
                  items: [...owners, managedOtherOwner],
                  onChanged: _saving ? null : (value) => setState(() => _choice = value),
                ),
              ),
              if (_choice == managedOtherOwner) ...[
                const SizedBox(height: 10),
                LabelledField(
                  label: 'Name of the tool',
                  child: KintsugiTextField(
                    controller: _other,
                    hintText: 'e.g. Intune',
                    enabled: !_saving,
                    onChanged: (_) => setState(() => _error = null),
                    onEditingComplete: _save,
                  ),
                ),
              ],
              const SizedBox(height: 14),
              const AlertBox.info(
                'Kintsugi will stop researching it with the AI and stop patching it, on every host and '
                'platform. A script it already has is kept, but no agent will run it, and it will not '
                'count as waiting for an update.',
              ),
            ],
            if (_error != null) ...[
              const SizedBox(height: 10),
              AlertBox.error(_error!),
            ],
          ],
        ),
      ),
      actions: [
        SecondaryButton(label: 'Cancel', onPressed: _saving ? null : () => Navigator.of(context).pop(false)),
        PrimaryButton(
          label: changing ? 'Save' : 'Mark as managed',
          busy: _saving,
          onPressed: owners == null ? null : _save,
        ),
      ],
    );
  }
}
