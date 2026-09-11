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

  /// The three numbers [widthFor] has to agree with. Named rather than written twice, because the
  /// measurement is only useful while it matches what [build] actually draws.
  static const _horizontalPadding = 10.0;
  static const _borderWidth = 1.0;

  static TextStyle _labelStyle(Color color) =>
      AppTheme.display(color: color, size: 10.4, letterSpacing: 0.83);

  /// How wide this chip will be carrying [label] — the uppercased label measured in the display
  /// face, plus the padding and border around it.
  ///
  /// For sizing a table column whose cells are chips. `KintsugiTable` floors every column at the
  /// longest *word* of its header label, which is what stops "ACTIONS" breaking to "ACTION" over
  /// "S" — but nothing measures the cells, and a chip is just as unbreakable: one uppercase token
  /// in a column too narrow for it wraps mid-word exactly the same way. The CVE Reporting table's
  /// Exploited column was hand-set to 120px and rendered "EXPLOITE" over "D", short by under two
  /// pixels, which is the kind of margin no number picked by eye can be trusted to keep.
  static double widthFor(BuildContext context, String label) {
    final painter = TextPainter(
      text: TextSpan(text: label.toUpperCase(), style: _labelStyle(const Color(0xFF000000))),
      textDirection: Directionality.of(context),
    )..layout();
    final labelWidth = painter.width;
    painter.dispose();

    return labelWidth + (_horizontalPadding + _borderWidth) * 2;
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final color = statusKey == '_accent' ? palette.neon : palette.forStatusKey(statusKey);

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: _horizontalPadding, vertical: 3),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        border: Border.all(color: color, width: _borderWidth),
        borderRadius: BorderRadius.circular(3),
      ),
      child: Text(
        label.toUpperCase(),
        style: _labelStyle(color),
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
