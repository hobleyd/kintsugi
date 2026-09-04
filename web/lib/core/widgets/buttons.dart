import 'package:flutter/material.dart';

import '../theme/app_theme.dart';
import '../theme/kintsugi_palette.dart';
import 'gradient_spinner.dart';

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
  const SecondaryButton({
    super.key,
    required this.label,
    required this.onPressed,
    this.tooltip,
    this.busy = false,
  });

  final String label;
  final VoidCallback? onPressed;
  final String? tooltip;

  /// Replaces the label with a spinner and blocks presses — [PrimaryButton.busy] for a secondary
  /// action. The label is kept in the layout, invisibly, so a button in a [Wrap] with a message
  /// beside it holds its width and nothing to its right moves when the spinner comes and goes.
  /// The border keeps its enabled colour for the same reason the spinner exists at all: a disabled
  /// look says "you may not press this", where this state says "you did, and it is running".
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final enabled = onPressed != null && !busy;
    final labelText = Text(
      label.toUpperCase(),
      style: AppTheme.display(
        color: enabled ? palette.neonDim : palette.muted,
        size: 10.9,
        letterSpacing: 0.87,
      ),
    );
    final button = OutlinedButton(
      onPressed: enabled ? onPressed : null,
      style: OutlinedButton.styleFrom(
        foregroundColor: palette.neon,
        side: BorderSide(color: enabled || busy ? palette.border : palette.border.withValues(alpha: 0.5)),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(3)),
      ),
      child: busy
          ? Stack(
              alignment: Alignment.center,
              children: [
                Opacity(opacity: 0, child: labelText),
                GradientSpinner(color: palette.neon),
              ],
            )
          : labelText,
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
    this.busy = false,
  });

  final IconData icon;
  final VoidCallback? onPressed;
  final String tooltip;

  /// Red rather than accent-coloured, for the one action that cannot be undone from here.
  final bool danger;

  /// Replaces the icon with a spinner and blocks presses — [PrimaryButton.busy] for a table row.
  /// The spinner is drawn *as* the button's icon rather than beside it, so a row that swaps
  /// between the two moves nothing: Material 3 lays an `IconButton` out at 40px whatever
  /// [constraints] below say, and a stand-in sized to that figure would drift with the theme.
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final color = danger ? palette.red : palette.neonDim;
    return IconButton(
      onPressed: busy ? null : onPressed,
      tooltip: tooltip,
      icon: busy
          ? SizedBox(
              width: 14,
              height: 14,
              child: CircularProgressIndicator(strokeWidth: 2, color: color),
            )
          : Icon(icon, size: 18),
      color: color,
      disabledColor: busy ? color : null,
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
