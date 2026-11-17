import 'package:flutter/material.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/application.dart';
import '../../domain/usecases/application_usecases.dart';
import 'patch_health_model.dart';

/// Confirms and sends "patch these hosts now". Completes with the server's answer, or null when the
/// administrator cancelled or it failed (the dialog says why before it closes).
///
/// The wording is deliberate. The instruction goes to every host named, and each host decides on its
/// own what to do about the person at it, so this dialog cannot say which hosts will show a warning
/// and says what the rule is instead.
Future<ForcedPatchRunResult?> showPatchNowDialog(BuildContext context, List<HealthItem> items) =>
    showDialog<ForcedPatchRunResult>(
      context: context,
      builder: (_) => _PatchNowDialog(items: items),
    );

class _PatchNowDialog extends StatefulWidget {
  const _PatchNowDialog({required this.items});

  final List<HealthItem> items;

  @override
  State<_PatchNowDialog> createState() => _PatchNowDialogState();
}

class _PatchNowDialogState extends State<_PatchNowDialog> {
  bool _sending = false;
  String? _error;

  Future<void> _send() async {
    setState(() {
      _sending = true;
      _error = null;
    });
    try {
      final result = await locator<RequestForcedPatchRuns>()(
        applicationName: windowsPatchNowName,
        platform: windowsPatchNowPlatform,
        hostNames: [for (final item in widget.items) item.hostname],
      );
      if (mounted) Navigator.of(context).pop(result);
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _sending = false;
          _error = error.message;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final count = widget.items.length;
    final anyOffline = widget.items.any((i) => i.kind == HealthKind.offline);

    return AlertDialog(
      backgroundColor: palette.backgroundAlt,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(6),
        side: BorderSide(color: palette.border),
      ),
      title: Text(
        count == 1 ? 'Patch ${widget.items.single.hostname} now' : 'Patch $count hosts now',
        style: Theme.of(context).textTheme.titleLarge,
      ),
      content: SizedBox(
        width: 520,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const AlertBox.info(
                'Each host installs its pending Windows updates, and restarts only if Windows says it '
                'needs to. If somebody is working at it, they first get a 5-minute "save your work" '
                'warning that cannot be delayed; behind a locked screen the install starts right away. '
                'A restart gets Windows\' own 5-minute countdown, and is skipped while anybody signed in '
                'is locked or disconnected: the update then finishes at the next restart. A host runs '
                'this only while somebody is signed in to it; one nobody is signed in to keeps the '
                'request for 24 hours and starts when someone signs in.',
              ),
              if (anyOffline) ...[
                const SizedBox(height: 10),
                const HintText(
                  'A host that is offline keeps the request for 24 hours and starts when it next checks in.',
                ),
              ],
              const SizedBox(height: 12),
              for (final item in widget.items)
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 2),
                  child: Row(
                    children: [
                      Expanded(child: Text(item.hostname)),
                      HintText(item.kind == HealthKind.offline ? 'offline' : item.cause),
                    ],
                  ),
                ),
              const SizedBox(height: 10),
              const HintText(
                'Only the fixed Windows Update install runs. Nothing typed here is sent to the host.',
              ),
              if (_error != null) ...[
                const SizedBox(height: 10),
                AlertBox.error(_error!),
              ],
            ],
          ),
        ),
      ),
      actions: [
        SecondaryButton(label: 'Cancel', onPressed: _sending ? null : () => Navigator.of(context).pop()),
        PrimaryButton(
          label: count == 1 ? 'Patch now' : 'Patch $count hosts now',
          busy: _sending,
          onPressed: _send,
        ),
      ],
    );
  }
}
