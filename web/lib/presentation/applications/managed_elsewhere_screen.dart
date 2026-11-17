import 'package:flutter/material.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/managed_application.dart';
import '../../domain/usecases/managed_application_usecases.dart';

/// The applications another tool patches (CrowdStrike, Jamf Pro, NinjaOne, Windows Update…), grouped
/// by who patches them, and the list of tools an administrator can pick from.
///
/// Marking happens on the Applications screen, where the application is being looked at; this is
/// where it is reviewed, undone, and where the owner list is kept. Everything on it needs an
/// administrator session, and a signed-in user without one sees the server's refusal as it is.
class ManagedElsewhereScreen extends StatefulWidget {
  const ManagedElsewhereScreen({super.key});

  @override
  State<ManagedElsewhereScreen> createState() => _ManagedElsewhereScreenState();
}

class _ManagedElsewhereScreenState extends State<ManagedElsewhereScreen> {
  final _newOwner = TextEditingController();

  ManagedApplications? _data;
  String? _error;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _run(() => locator<GetManagedApplications>()());
  }

  @override
  void dispose() {
    _newOwner.dispose();
    super.dispose();
  }

  Future<void> _run(Future<ManagedApplications> Function() action) async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final data = await action();
      if (mounted) setState(() => _data = data);
    } on ApiException catch (error) {
      if (mounted) {
        setState(() => _error = error.validationErrors.isEmpty
            ? error.message
            : error.validationErrors.values.expand((m) => m).join(' '));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _addOwner() async {
    final name = _newOwner.text.trim();
    if (name.isEmpty) return;
    await _run(() => locator<AddManagedOwner>()(name));
    if (_error == null) _newOwner.clear();
  }

  @override
  Widget build(BuildContext context) {
    final data = _data;

    return PageScaffold(
      title: 'Managed Elsewhere',
      children: [
        SectionHeader(
          title: 'Applications Another Tool Patches',
          hints: const [
            HintText(
              'Kintsugi does not research, check or patch these, on any host or platform, and does '
              'not count them as waiting for an update. They are still inventoried, so they stay in '
              'Currently Installed and in CVE reporting.',
            ),
            HintText(
              'Mark an application from Currently Installed with the shield beside its row. It is '
              'matched by name, so every platform and every spelling merged into that name is '
              'covered.',
            ),
          ],
          actions: [
            SecondaryButton(
              label: 'Reload',
              onPressed: _busy ? null : () => _run(() => locator<GetManagedApplications>()()),
            ),
          ],
        ),
        if (_error != null) ...[AlertBox.error(_error!), const SizedBox(height: 12)],
        if (data == null)
          EmptyPanel(_error == null ? 'Loading…' : 'Could not load the list.')
        else ...[
          _OwnersPanel(
            data: data,
            busy: _busy,
            controller: _newOwner,
            onAdd: _addOwner,
            onRemove: (owner) => _run(() => locator<RemoveManagedOwner>()(owner)),
          ),
          const SizedBox(height: 20),
          if (data.applications.isEmpty)
            const EmptyPanel('No application is marked as patched by another tool.')
          else
            for (final owner in _ownersWithApplications(data)) ...[
              _OwnerGroup(
                owner: owner,
                applications: [for (final a in data.applications) if (a.owner == owner) a],
                busy: _busy,
                onRelease: (name) => _run(() => locator<UnmarkApplicationManaged>()(name)),
              ),
              const SizedBox(height: 16),
            ],
        ],
      ],
    );
  }

  /// Owners in the order the server sent the applications, so the page reads as the API ordered it.
  static List<String> _ownersWithApplications(ManagedApplications data) =>
      {for (final a in data.applications) a.owner}.toList();
}

class _OwnersPanel extends StatelessWidget {
  const _OwnersPanel({
    required this.data,
    required this.busy,
    required this.controller,
    required this.onAdd,
    required this.onRemove,
  });

  final ManagedApplications data;
  final bool busy;
  final TextEditingController controller;
  final VoidCallback onAdd;
  final void Function(String owner) onRemove;

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        child: Padding(padding: const EdgeInsets.all(16), child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Tools that can own an application', style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 4),
            const HintText(
              'The choices offered when marking an application. A tool cannot be removed while an '
              'application is still marked as patched by it.',
            ),
            const SizedBox(height: 12),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final owner in data.owners)
                  InputChip(
                    label: Text(owner),
                    onDeleted: busy || data.ownerIsInUse(owner) ? null : () => onRemove(owner),
                    deleteButtonTooltipMessage:
                        data.ownerIsInUse(owner) ? 'In use — release its applications first' : 'Remove',
                  ),
              ],
            ),
            const SizedBox(height: 14),
            SizedBox(
              width: 420,
              child: Row(
                children: [
                  Expanded(
                    child: KintsugiTextField(
                      controller: controller,
                      hintText: 'Add a tool, e.g. Intune',
                      enabled: !busy,
                      onEditingComplete: onAdd,
                    ),
                  ),
                  const SizedBox(width: 10),
                  SecondaryButton(label: 'Add', onPressed: busy ? null : onAdd),
                ],
              ),
            ),
          ],
        )),
      );
}

class _OwnerGroup extends StatelessWidget {
  const _OwnerGroup({
    required this.owner,
    required this.applications,
    required this.busy,
    required this.onRelease,
  });

  final String owner;
  final List<ManagedApplication> applications;
  final bool busy;
  final void Function(String name) onRelease;

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        child: Padding(padding: const EdgeInsets.all(16), child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              '$owner · ${applications.length}',
              style: Theme.of(context).textTheme.titleMedium?.copyWith(color: context.palette.neon),
            ),
            const SizedBox(height: 8),
            for (final application in applications)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 4),
                child: Row(
                  children: [
                    Expanded(child: Text(application.applicationName)),
                    HintText('${application.hostCount} host(s)'),
                    const SizedBox(width: 12),
                    SecondaryButton(
                      label: 'Stop treating as managed',
                      onPressed: busy ? null : () => onRelease(application.applicationName),
                    ),
                  ],
                ),
              ),
          ],
        )),
      );
}
