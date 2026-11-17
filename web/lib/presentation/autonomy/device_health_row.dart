import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/device_report.dart';

/// One device in the Device health panel: the bordered row with the hostname in bold, the reason under
/// it, `last <date>` and a status chip on the right, and the "Open report" link. The flagged list and the
/// "Find a device" results both draw it, so the two cannot drift apart.
///
/// Tapping the row calls [onToggle] when given, and shows [expanded] beneath it.
class DeviceHealthRowCard extends StatelessWidget {
  const DeviceHealthRowCard({
    super.key,
    required this.hostname,
    required this.detail,
    required this.status,
    required this.lastEventUtc,
    required this.onOpenReport,
    this.onToggle,
    this.expanded,
  });

  final String hostname;
  final String detail;
  final DeviceHealthStatus status;
  final DateTime? lastEventUtc;
  final VoidCallback onOpenReport;
  final VoidCallback? onToggle;
  final Widget? expanded;

  /// The word and the status key of the chip. A healthy device gets the green one; the word is always
  /// there, so the colour is never the only signal.
  static (String, String) chipFor(DeviceHealthStatus status) => switch (status) {
        DeviceHealthStatus.unstable => ('UNSTABLE', 'patch-failed'),
        DeviceHealthStatus.watch => ('WATCH', 'update-available'),
        DeviceHealthStatus.healthy => ('HEALTHY', 'up-to-date'),
      };

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final stamp = DateFormat('yyyy-MM-dd HH:mm');
    final (label, key) = chipFor(status);

    return Padding(
      padding: const EdgeInsets.only(top: 10),
      child: Container(
        decoration: BoxDecoration(border: Border.all(color: palette.border), borderRadius: BorderRadius.circular(4)),
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          InkWell(
            onTap: onToggle,
            child: Padding(
              padding: const EdgeInsets.all(12),
              child: Row(children: [
                Expanded(
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(hostname, style: const TextStyle(fontWeight: FontWeight.w600)),
                    HintText(detail),
                  ]),
                ),
                if (lastEventUtc != null) HintText('last ${stamp.format(lastEventUtc!.toLocal())}'),
                const SizedBox(width: 12),
                StatusChip(label, statusKey: key),
                const SizedBox(width: 12),
                LinkText(label: 'Open report', onTap: onOpenReport),
              ]),
            ),
          ),
          if (expanded != null)
            Padding(padding: const EdgeInsets.fromLTRB(16, 0, 16, 14), child: expanded),
        ]),
      ),
    );
  }
}
