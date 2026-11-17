import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'autonomy_report.dart';

/// The report as it will read, drawn like a page of paper. The same [ReportDocument] is written to the
/// PDF, so what is checked here is what is downloaded.
class ReportPreview extends StatelessWidget {
  const ReportPreview({super.key, required this.document});

  final ReportDocument document;

  static const ink = Color(0xFF14202B);
  static const muted = Color(0xFF5D7280);
  static const accent = Color(0xFF0B3A4A);
  static const rule = Color(0xFF9FB4BF);
  static const own = Color(0xFF0891B2);
  static const admin = Color(0xFFB26B00);
  static const red = Color(0xFFC0183F);
  static const green = Color(0xFF2E7D3E);

  static Color statusColor(String s) => switch (s) {
        'R' => red,
        'A' => admin,
        'G' => green,
        _ => muted,
      };

  static TextStyle s({double size = 14, bool bold = false, Color color = ink}) =>
      TextStyle(fontSize: size, color: color, fontWeight: bold ? FontWeight.w700 : FontWeight.w400, height: 1.4);

  static const _empty = ReportDocument(title: '', subtitle: '', sections: []);

  /// One block on its own, for a place that is not the report page (the trends on the Autonomy page).
  static Widget blockWidget(ReportBlock b) => const ReportPreview(document: _empty)._block(b);

  @override
  Widget build(BuildContext context) => Center(
        child: Container(
          constraints: const BoxConstraints(maxWidth: 920),
          padding: const EdgeInsets.symmetric(horizontal: 44, vertical: 36),
          color: Colors.white,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(document.title, style: s(size: 26, bold: true, color: accent)),
              Text(document.subtitle, style: s(size: 13, color: muted)),
              for (final section in document.sections) ...[
                const SizedBox(height: 20),
                Text(section.title, style: s(size: 17, bold: true, color: accent)),
                const Divider(color: accent, thickness: 2, height: 8),
                for (final b in section.blocks) _block(b),
              ],
            ],
          ),
        ),
      );

  Widget _block(ReportBlock b) => switch (b) {
        ReportText() => Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Text(b.text, style: s(color: b.muted ? muted : ink, bold: b.bold, size: b.muted ? 12.5 : 14)),
          ),
        ReportBullets() => Padding(
            padding: const EdgeInsets.only(top: 4, left: 12),
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [for (final i in b.items) Text('•  $i', style: s())]),
          ),
        ReportStats() => Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Wrap(spacing: 8, runSpacing: 8, children: [
              for (final (value, label) in b.items)
                Container(
                  width: 190,
                  padding: const EdgeInsets.all(10),
                  decoration: BoxDecoration(border: Border.all(color: rule.withValues(alpha: 0.6))),
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(value, style: s(size: 22, bold: true, color: accent)),
                    Text(label, style: s(size: 12, color: muted)),
                  ]),
                ),
            ]),
          ),
        ReportTable() => _table(b),
        ReportColumns() => _columns(b),
        ReportBars() => _bars(b),
        ReportLine() => _line(b),
      };

  Widget _table(ReportTable b) {
    final size = b.headers.length >= 8 ? 11.0 : 12.5;
    return Padding(
      padding: const EdgeInsets.only(top: 6),
      child: Table(
        columnWidths: b.weights == null ? null : {for (var i = 0; i < b.weights!.length; i++) i: FlexColumnWidth(b.weights![i])},
        border: TableBorder(horizontalInside: BorderSide(color: rule.withValues(alpha: 0.5)), bottom: BorderSide(color: rule.withValues(alpha: 0.5))),
        children: [
          TableRow(children: [for (final h in b.headers) Padding(padding: const EdgeInsets.all(4), child: Text(h.toUpperCase(), style: s(size: size - 1.5, bold: true, color: muted)))]),
          for (var r = 0; r < b.rows.length; r++)
            TableRow(children: [
              for (var c = 0; c < b.rows[r].length; c++)
                Padding(
                  padding: const EdgeInsets.all(4),
                  child: Text(
                    b.rows[r][c],
                    style: b.statuses[c] != null ? s(size: size, bold: true, color: statusColor(b.statuses[c]![r])) : s(size: size),
                  ),
                ),
            ]),
        ],
      ),
    );
  }

  Widget _legend(ReportColumns b) => Wrap(crossAxisAlignment: WrapCrossAlignment.center, spacing: 6, children: [
        Container(width: 9, height: 9, color: own),
        Text(b.ownLabel, style: s(size: 11, color: muted)),
        Container(width: 9, height: 9, color: admin),
        Text(b.adminLabel, style: s(size: 11, color: muted)),
      ]);

  Widget _columns(ReportColumns b) {
    const area = 100.0;
    final maxTotal = b.bars.fold<int>(1, (m, x) => math.max(m, x.$2 + x.$3));
    final every = b.bars.length <= 14 ? 1 : (b.bars.length / 8).ceil();

    return Padding(
      padding: const EdgeInsets.only(top: 10),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Wrap(spacing: 12, children: [Text(b.title, style: s(size: 13, bold: true)), _legend(b)]),
        const SizedBox(height: 4),
        SizedBox(
          height: area + 20,
          child: Row(crossAxisAlignment: CrossAxisAlignment.end, children: [
            for (final bar in b.bars)
              Expanded(
                child: Tooltip(
                  message: '${bar.$1}: ${bar.$2 + bar.$3} (${bar.$2} ${b.ownLabel}, ${bar.$3} ${b.adminLabel})',
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 1),
                    child: Column(mainAxisAlignment: MainAxisAlignment.end, children: [
                      if (b.bars.length <= 14) Text('${bar.$2 + bar.$3}', style: s(size: 10)),
                      if (bar.$3 > 0) Container(height: bar.$3 / maxTotal * area, color: admin),
                      Container(height: math.max(1, bar.$2 / maxTotal * area), color: own),
                    ]),
                  ),
                ),
              ),
          ]),
        ),
        Row(children: [
          for (var i = 0; i < b.bars.length; i++)
            Expanded(child: Text(i % every == 0 ? b.bars[i].$1 : '', textAlign: TextAlign.center, maxLines: 1, overflow: TextOverflow.clip, style: s(size: 10, color: muted))),
        ]),
      ]),
    );
  }

  Widget _bars(ReportBars b) {
    final maxValue = b.items.fold<double>(1, (m, x) => math.max(m, x.$2));
    return Padding(
      padding: const EdgeInsets.only(top: 10),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text(b.title, style: s(size: 13, bold: true)),
        const SizedBox(height: 4),
        for (final item in b.items)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 2),
            child: Row(children: [
              SizedBox(width: 230, child: Text(item.$1, textAlign: TextAlign.right, style: s(size: 12))),
              const SizedBox(width: 8),
              Expanded(
                child: Align(
                  alignment: Alignment.centerLeft,
                  child: FractionallySizedBox(
                    widthFactor: math.max(0.01, item.$2 / maxValue) * 0.85,
                    child: Container(height: 12, color: own),
                  ),
                ),
              ),
              Text('${item.$2}${b.unit}', style: s(size: 12, bold: true)),
            ]),
          ),
      ]),
    );
  }

  Widget _line(ReportLine b) => Padding(
        padding: const EdgeInsets.only(top: 10),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Wrap(spacing: 10, children: [
            Text(b.title, style: s(size: 13, bold: true)),
            if (b.target != null) Text('- - ${b.targetLabel}', style: s(size: 11, color: green)),
          ]),
          SizedBox(height: 130, child: CustomPaint(size: Size.infinite, painter: _LinePainter(b))),
        ]),
      );
}

class _LinePainter extends CustomPainter {
  _LinePainter(this.line);

  final ReportLine line;

  @override
  void paint(Canvas canvas, Size size) {
    const left = 36.0;
    const bottom = 18.0;
    const top = 8.0;
    final w = size.width - left - 8;
    final h = size.height - bottom - top;
    final values = line.values;
    if (values.length < 2) return;

    final all = [...values, ?line.target];
    final lo = (all.reduce(math.min) * 0.9).floorToDouble();
    final hi = (all.reduce(math.max) * 1.1).ceilToDouble();
    double x(int i) => left + w * i / (values.length - 1);
    double y(double v) => top + h - h * (v - lo) / (hi == lo ? 1 : hi - lo);

    final grid = Paint()..color = ReportPreview.rule.withValues(alpha: 0.4);
    void text(String t, Offset at, {Color color = ReportPreview.muted, bool bold = false, TextAlign align = TextAlign.left}) {
      final p = TextPainter(
        text: TextSpan(text: t, style: TextStyle(fontSize: 10, color: color, fontWeight: bold ? FontWeight.w700 : FontWeight.w400)),
        textDirection: TextDirection.ltr,
      )..layout();
      final dx = align == TextAlign.right ? at.dx - p.width : align == TextAlign.center ? at.dx - p.width / 2 : at.dx;
      p.paint(canvas, Offset(dx, at.dy));
    }

    String fmt(double v) => '${v == v.roundToDouble() ? v.toInt() : v}${line.suffix}';
    for (final v in [lo, (lo + hi) / 2, hi]) {
      canvas.drawLine(Offset(left, y(v)), Offset(left + w, y(v)), grid);
      text(fmt(v), Offset(left - 4, y(v) - 6), align: TextAlign.right);
    }

    if (line.target != null) {
      final dash = Paint()
        ..color = ReportPreview.green
        ..strokeWidth = 1.4;
      for (var dx = left; dx < left + w; dx += 9) {
        canvas.drawLine(Offset(dx, y(line.target!)), Offset(math.min(dx + 5, left + w), y(line.target!)), dash);
      }
    }

    final path = Path()..moveTo(x(0), y(values[0]));
    for (var i = 1; i < values.length; i++) {
      path.lineTo(x(i), y(values[i]));
    }
    canvas.drawPath(
      path,
      Paint()
        ..color = ReportPreview.own
        ..style = PaintingStyle.stroke
        ..strokeWidth = 2,
    );

    final lastI = values.length - 1;
    canvas.drawCircle(Offset(x(lastI), y(values[lastI])), 4, Paint()..color = ReportPreview.own);
    text(fmt(values[lastI]), Offset(x(lastI) - 4, y(values[lastI]) - 18), color: ReportPreview.ink, bold: true, align: TextAlign.right);
    for (final i in [0, lastI ~/ 2, lastI]) {
      text(line.labels[i], Offset(x(i), size.height - 13), align: i == 0 ? TextAlign.left : i == lastI ? TextAlign.right : TextAlign.center);
    }
  }

  @override
  bool shouldRepaint(_LinePainter old) => old.line != line;
}

/// A single report block drawn on a white card, so a chart made for the page reads the same on the
/// dark Autonomy screen.
class ReportPreviewBlock extends StatelessWidget {
  const ReportPreviewBlock({super.key, required this.block});

  final ReportBlock block;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(top: 10),
        child: Container(
          color: Colors.white,
          padding: const EdgeInsets.fromLTRB(14, 4, 14, 12),
          child: ReportPreview.blockWidget(block),
        ),
      );
}
