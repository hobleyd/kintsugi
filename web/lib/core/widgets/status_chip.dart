import 'package:flutter/material.dart';

import '../theme/app_theme.dart';
import '../theme/kintsugi_palette.dart';

/// The small uppercase outlined badge a status is shown as — `.status`.
///
/// Coloured from the palette by status key rather than by a colour passed in, so "up to date" is
/// the same green everywhere it appears and a new status added server-side lands on the muted
/// default rather than on whatever the nearest call site happened to pick.
class StatusChip extends StatelessWidget {
  const StatusChip(this.label, {super.key, required this.statusKey});

  /// A chip in the accent colour, for a neutral fact rather than a state.
  const StatusChip.neutral(this.label, {super.key}) : statusKey = '_accent';

  final String label;
  final String statusKey;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final color = statusKey == '_accent' ? palette.neon : palette.forStatusKey(statusKey);

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        border: Border.all(color: color),
        borderRadius: BorderRadius.circular(3),
      ),
      child: Text(
        label.toUpperCase(),
        style: AppTheme.display(color: color, size: 10.4, letterSpacing: 0.83),
      ),
    );
  }
}

/// A right-aligned count in a bordered box — `.count-badge`. [alert] switches it to amber and
/// makes it tappable, which is how the Hosts screen's "N app updates" deep-links into a filtered
/// Applications view.
class CountBadge extends StatelessWidget {
  const CountBadge(this.count, {super.key, this.alert = false, this.onTap, this.tooltip});

  final int count;
  final bool alert;
  final VoidCallback? onTap;
  final String? tooltip;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final color = alert ? palette.amber : palette.neon;

    Widget badge = Container(
      constraints: const BoxConstraints(minWidth: 30),
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        border: Border.all(color: alert ? color : palette.border),
        borderRadius: BorderRadius.circular(3),
      ),
      child: Text(
        '$count',
        textAlign: TextAlign.center,
        style: AppTheme.display(color: color, size: 12, weight: FontWeight.w600, letterSpacing: 0.4),
      ),
    );

    if (onTap != null) {
      badge = MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(onTap: onTap, child: badge),
      );
    }
    if (tooltip != null) {
      badge = Tooltip(message: tooltip!, child: badge);
    }
    return badge;
  }
}
