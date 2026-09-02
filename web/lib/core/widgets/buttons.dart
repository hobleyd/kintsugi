import 'package:flutter/material.dart';

import '../theme/app_theme.dart';
import '../theme/kintsugi_palette.dart';

/// The filled accent button — `.btn-primary`. One per screen, on the action the screen is for.
class PrimaryButton extends StatelessWidget {
  const PrimaryButton({super.key, required this.label, required this.onPressed, this.busy = false});

  final String label;
  final VoidCallback? onPressed;

  /// Shows a spinner beside the label and blocks presses. Used while a background run is going,
  /// where the label also changes ("Scan Running…"), so the two together make it obvious the
  /// button is not simply broken.
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final enabled = onPressed != null && !busy;

    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(3),
        boxShadow: palette.glowsEnabled && enabled
            ? [BoxShadow(color: palette.neon.withValues(alpha: 0.55), blurRadius: 14)]
            : const [],
      ),
      child: FilledButton(
        onPressed: enabled ? onPressed : null,
        style: FilledButton.styleFrom(
          backgroundColor: palette.neon,
          disabledBackgroundColor: palette.neon.withValues(alpha: 0.4),
          foregroundColor: palette.background,
          padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 16),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(3)),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (busy) ...[
              SizedBox(
                width: 12,
                height: 12,
                child: CircularProgressIndicator(strokeWidth: 2, color: palette.background),
              ),
              const SizedBox(width: 8),
            ],
            Text(label.toUpperCase(), style: AppTheme.display(color: palette.background, size: 12)),
          ],
        ),
      ),
    );
  }
}

/// The outlined button — `.btn-secondary`. Everything that is not the screen's main action.
class SecondaryButton extends StatelessWidget {
  const SecondaryButton({super.key, required this.label, required this.onPressed, this.tooltip});

  final String label;
  final VoidCallback? onPressed;
  final String? tooltip;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final button = OutlinedButton(
      onPressed: onPressed,
      style: OutlinedButton.styleFrom(
        foregroundColor: palette.neon,
        side: BorderSide(color: onPressed == null ? palette.border.withValues(alpha: 0.5) : palette.border),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(3)),
      ),
      child: Text(
        label.toUpperCase(),
        style: AppTheme.display(
          color: onPressed == null ? palette.muted : palette.neonDim,
          size: 10.9,
          letterSpacing: 0.87,
        ),
      ),
    );

    return tooltip == null ? button : Tooltip(message: tooltip!, child: button);
  }
}

/// A bare icon button in a table's action column — `.icon-btn`.
class IconActionButton extends StatelessWidget {
  const IconActionButton({
    super.key,
    required this.icon,
    required this.onPressed,
    required this.tooltip,
    this.danger = false,
  });

  final IconData icon;
  final VoidCallback? onPressed;
  final String tooltip;

  /// Red rather than accent-coloured, for the one action that cannot be undone from here.
  final bool danger;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return IconButton(
      onPressed: onPressed,
      tooltip: tooltip,
      icon: Icon(icon, size: 18),
      color: danger ? palette.red : palette.neonDim,
      hoverColor: (danger ? palette.red : palette.neon).withValues(alpha: 0.1),
      constraints: const BoxConstraints(minWidth: 34, minHeight: 34),
      padding: EdgeInsets.zero,
      visualDensity: VisualDensity.compact,
    );
  }
}

/// Text that behaves like a link — `.link-btn` and `.upgrade-link`.
class LinkText extends StatelessWidget {
  const LinkText({super.key, required this.label, required this.onTap, this.muted = false});

  final String label;
  final VoidCallback onTap;
  final bool muted;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: onTap,
        child: Text(
          label,
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                color: muted ? palette.muted : palette.neon,
                fontSize: muted ? 12.8 : null,
                decoration: TextDecoration.underline,
                decorationColor: (muted ? palette.muted : palette.neon).withValues(alpha: 0.5),
              ),
        ),
      ),
    );
  }
}
