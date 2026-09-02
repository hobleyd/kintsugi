import 'package:flutter/material.dart';

import '../theme/kintsugi_palette.dart';

/// The bordered, tinted surface everything on a screen sits inside — `.panel`.
class KintsugiPanel extends StatelessWidget {
  const KintsugiPanel({super.key, required this.child, this.padding, this.clip = true});

  final Widget child;
  final EdgeInsetsGeometry? padding;

  /// Whether to clip to the rounded corners. Tables want this so a row's hover tint does not
  /// square off the panel; a panel holding an overflowing menu does not.
  final bool clip;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return Container(
      clipBehavior: clip ? Clip.antiAlias : Clip.none,
      padding: padding,
      decoration: BoxDecoration(
        color: palette.panel,
        border: Border.all(color: palette.border),
        borderRadius: BorderRadius.circular(6),
        boxShadow: palette.glowsEnabled
            ? [BoxShadow(color: palette.accentWash(0.08), blurRadius: 30)]
            : [BoxShadow(color: Colors.black.withValues(alpha: 0.06), blurRadius: 18, offset: const Offset(0, 6))],
      ),
      child: child,
    );
  }
}

/// A panel whose only content is a sentence explaining why it is empty — `.empty`.
class EmptyPanel extends StatelessWidget {
  const EmptyPanel(this.message, {super.key, this.trailing});

  final String message;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 40),
        child: Column(
          children: [
            Text(
              message,
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: context.palette.muted),
            ),
            if (trailing != null) ...[const SizedBox(height: 12), trailing!],
          ],
        ),
      );
}
