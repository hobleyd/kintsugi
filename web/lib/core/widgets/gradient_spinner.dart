import 'dart:math' as math;

import 'package:flutter/widgets.dart';

/// A spinning arc that fades from transparent at its tail to [color] at its head.
///
/// Drawn by hand rather than with `CircularProgressIndicator` because Material's ring is a solid
/// stroke and the accent here is a bright cyan: a solid cyan ring inside an outlined button reads
/// as a second border, where a gradient tail reads as motion. The rotation is a
/// [RotationTransition] over an unchanging painter, so each frame is a transform rather than a
/// repaint.
class GradientSpinner extends StatefulWidget {
  const GradientSpinner({super.key, required this.color, this.size = 14, this.strokeWidth = 2});

  final Color color;
  final double size;
  final double strokeWidth;

  @override
  State<GradientSpinner> createState() => _GradientSpinnerState();
}

class _GradientSpinnerState extends State<GradientSpinner> with SingleTickerProviderStateMixin {
  late final AnimationController _turns = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 900),
  )..repeat();

  @override
  void dispose() {
    _turns.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => RotationTransition(
        turns: _turns,
        child: CustomPaint(
          size: Size.square(widget.size),
          painter: _GradientArcPainter(color: widget.color, strokeWidth: widget.strokeWidth),
        ),
      );
}

class _GradientArcPainter extends CustomPainter {
  const _GradientArcPainter({required this.color, required this.strokeWidth});

  final Color color;
  final double strokeWidth;

  // The gap left open between the head and the tail, so the fade has somewhere to end rather than
  // wrapping into the head and hiding it.
  static const double _gap = math.pi / 4;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = (Offset.zero & size).deflate(strokeWidth / 2);
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = strokeWidth
      ..strokeCap = StrokeCap.round
      ..shader = SweepGradient(
        colors: [color.withValues(alpha: 0), color],
        endAngle: 2 * math.pi - _gap,
      ).createShader(rect);

    canvas.drawArc(rect, 0, 2 * math.pi - _gap, false, paint);
  }

  @override
  bool shouldRepaint(_GradientArcPainter oldDelegate) =>
      oldDelegate.color != color || oldDelegate.strokeWidth != strokeWidth;
}
