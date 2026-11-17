import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/device_report.dart';
import 'device_report_model.dart';

/// The shapes on the timeline. They differ as well as the colours, so nothing depends on colour alone:
/// a square for a shutdown, a filled circle for a fatal error, a ring for a corrected one, a filled
/// diamond for an install and a hollow diamond for a system update.
enum MarkShape { square, disc, ring, diamond, diamondRing }

class _MarkPainter extends CustomPainter {
  _MarkPainter(this.shape, this.color, this.selected);

  final MarkShape shape;
  final Color color;
  final bool selected;

  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    final r = size.shortestSide / 2 - 1;
    final fill = Paint()..color = color;
    final line = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 2;

    Path diamond(double radius) => Path()
      ..moveTo(c.dx, c.dy - radius)
      ..lineTo(c.dx + radius, c.dy)
      ..lineTo(c.dx, c.dy + radius)
      ..lineTo(c.dx - radius, c.dy)
      ..close();

    switch (shape) {
      case MarkShape.square:
        canvas.drawRRect(RRect.fromRectAndRadius(Rect.fromCircle(center: c, radius: r - 1), const Radius.circular(1.5)), fill);
      case MarkShape.disc:
        canvas.drawCircle(c, r - 1, fill);
      case MarkShape.ring:
        canvas.drawCircle(c, r - 1.5, line);
      case MarkShape.diamond:
        canvas.drawPath(diamond(r + 1), fill);
      case MarkShape.diamondRing:
        canvas.drawPath(diamond(r), line);
    }

    if (selected) {
      canvas.drawCircle(
        c,
        size.shortestSide / 2,
        Paint()
          ..color = color
          ..style = PaintingStyle.stroke
          ..strokeWidth = 1,
      );
    }
  }

  @override
  bool shouldRepaint(_MarkPainter old) => old.shape != shape || old.color != color || old.selected != selected;
}

/// One mark, drawn the size it is on the chart.
class _Mark extends StatelessWidget {
  const _Mark(this.shape, this.color, {this.selected = false, this.size = 16});

  final MarkShape shape;
  final Color color;
  final bool selected;
  final double size;

  @override
  Widget build(BuildContext context) =>
      CustomPaint(size: Size.square(size), painter: _MarkPainter(shape, color, selected));
}

MarkShape _shapeOf(DeviceReportEventKind kind) => switch (kind) {
      DeviceReportEventKind.unexpectedShutdown => MarkShape.square,
      DeviceReportEventKind.hardwareErrorFatal => MarkShape.disc,
      DeviceReportEventKind.hardwareErrorCorrected => MarkShape.ring,
    };

Color _colorOf(KintsugiPalette p, DeviceReportEventKind kind) =>
    kind == DeviceReportEventKind.hardwareErrorCorrected ? p.amber : p.red;

/// Three lanes over the last 7 or 30 days: shutdowns, hardware errors, and what Kintsugi installed.
/// A shutdown or hardware mark is clicked to select it; every mark says what it is on hover.
class DeviceTimeline extends StatelessWidget {
  const DeviceTimeline({
    super.key,
    required this.report,
    required this.now,
    required this.selected,
    required this.onSelect,
  });

  final DeviceReport report;

  /// The right-hand edge of the chart.
  final DateTime now;
  final DeviceReportEvent? selected;
  final void Function(DeviceReportEvent event) onSelect;

  static const _labelWidth = 92.0;
  static const _rightPad = 14.0;
  static const _height = 150.0;
  static const _lanes = [38.0, 78.0, 118.0];
  static const _markSize = 16.0;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final window = report.days * 24 * 3600 * 1000;

    return LayoutBuilder(
      builder: (context, box) {
        final plotWidth = (box.maxWidth - _labelWidth - _rightPad).clamp(80.0, double.infinity);

        double x(DateTime t) {
          final age = now.difference(t).inMilliseconds;
          return _labelWidth + (1 - age / window).clamp(0.0, 1.0) * plotWidth;
        }

        Widget at(double left, double top, Widget child) =>
            Positioned(left: left - _markSize / 2, top: top - _markSize / 2, child: child);

        final midnights = <DateTime>[
          for (var back = 0; back <= report.days; back++) DateTime(now.year, now.month, now.day - back),
        ].where((d) => !d.isBefore(now.subtract(Duration(hours: report.days * 24)))).toList();
        final labelEvery = report.days <= 7 ? 1 : 5;
        final dayLabel = DateFormat('MMM d');

        return SizedBox(
          height: _height,
          width: double.infinity,
          child: Stack(
            clipBehavior: Clip.none,
            children: [
              for (final (i, lane) in ['Shutdowns', 'Hardware', 'Kintsugi'].indexed) ...[
                Positioned(
                  left: _labelWidth,
                  right: _rightPad,
                  top: _lanes[i],
                  child: Container(height: 1, color: palette.border),
                ),
                Positioned(left: 0, top: _lanes[i] - 8, width: _labelWidth - 8, child: HintText(lane)),
              ],
              for (final (i, d) in midnights.indexed) ...[
                Positioned(left: x(d), top: 12, bottom: 24, child: Container(width: 1, color: palette.border.withValues(alpha: 0.3))),
                if (i % labelEvery == 0)
                  Positioned(
                    left: x(d) - 24,
                    bottom: 2,
                    width: 48,
                    child: Text(dayLabel.format(d), textAlign: TextAlign.center, style: TextStyle(fontSize: 11, color: palette.muted)),
                  ),
              ],
              for (final a in report.kintsugiActions)
                at(
                  x(a.atUtc),
                  _lanes[2],
                  Tooltip(
                    message: 'Kintsugi ${kintsugiActionLabel(a.kind)}\n${stampLabel(a.atUtc)}\n${a.subject}',
                    child: _Mark(
                      a.kind == KintsugiActionKind.osUpdate ? MarkShape.diamondRing : MarkShape.diamond,
                      a.kind == KintsugiActionKind.osUpdate ? palette.neon : palette.green,
                    ),
                  ),
                ),
              for (final e in report.events)
                at(
                  x(e.occurredUtc),
                  e.isShutdown ? _lanes[0] : _lanes[1],
                  Tooltip(
                    message: '${e.kind.label}\n${stampLabel(e.occurredUtc)}\n${e.source}${e.detail == null ? '' : '\n${e.detail}'}',
                    child: MouseRegion(
                      cursor: SystemMouseCursors.click,
                      child: GestureDetector(
                        key: ValueKey('mark-${e.occurredUtc.microsecondsSinceEpoch}-${e.kind.name}'),
                        behavior: HitTestBehavior.opaque,
                        onTap: () => onSelect(e),
                        child: _Mark(_shapeOf(e.kind), _colorOf(palette, e.kind), selected: e == selected),
                      ),
                    ),
                  ),
                ),
            ],
          ),
        );
      },
    );
  }
}

/// What each mark means, drawn with the mark itself.
class DeviceTimelineLegend extends StatelessWidget {
  const DeviceTimelineLegend({super.key});

  @override
  Widget build(BuildContext context) {
    final p = context.palette;
    Widget item(MarkShape shape, Color color, String label) => Row(mainAxisSize: MainAxisSize.min, children: [
          _Mark(shape, color, size: 13),
          const SizedBox(width: 6),
          HintText(label),
        ]);

    return Wrap(spacing: 18, runSpacing: 6, children: [
      item(MarkShape.square, p.red, 'Unexpected shutdown'),
      item(MarkShape.disc, p.red, 'Fatal hardware error'),
      item(MarkShape.ring, p.amber, 'Corrected hardware error'),
      item(MarkShape.diamond, p.green, 'Kintsugi installed an update'),
      item(MarkShape.diamondRing, p.neon, 'Kintsugi installed a system update (can restart the device)'),
    ]);
  }
}
