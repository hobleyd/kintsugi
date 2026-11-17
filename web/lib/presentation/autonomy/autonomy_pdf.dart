import 'dart:math' as math;
import 'dart:typed_data';

import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;

import 'autonomy_report.dart';

/// The colours a report is drawn in. One validated pair for the two series (own schedule, started by an
/// administrator) and the three status colours, which are reserved for status and always drawn with
/// their word beside them, never as colour alone.
abstract final class ReportColors {
  static const ink = PdfColor.fromInt(0xFF14202B);
  static const muted = PdfColor.fromInt(0xFF5D7280);
  static const accent = PdfColor.fromInt(0xFF0B3A4A);
  static const rule = PdfColor.fromInt(0xFF9FB4BF);
  static const own = PdfColor.fromInt(0xFF0891B2);
  static const admin = PdfColor.fromInt(0xFFB26B00);
  static const red = PdfColor.fromInt(0xFFC0183F);
  static const amber = PdfColor.fromInt(0xFFB26B00);
  static const green = PdfColor.fromInt(0xFF2E7D3E);

  static PdfColor forStatus(String s) => switch (s) {
        'R' => red,
        'A' => amber,
        'G' => green,
        _ => muted,
      };
}

/// Writes a [ReportDocument] as a PDF, in the browser, with nothing sent anywhere.
///
/// Uses the PDF format's built-in Helvetica, which covers Latin-1 only. A character outside it would
/// be dropped by the reader, so the few we write ("→", "…", dashes, "≤", "▲") are replaced first by
/// [pdfSafe].
Future<Uint8List> reportToPdf(ReportDocument report) async {
  final doc = pw.Document(title: report.title);

  pw.TextStyle style({double size = 10, bool bold = false, PdfColor color = ReportColors.ink}) => pw.TextStyle(
        fontSize: size,
        color: color,
        fontWeight: bold ? pw.FontWeight.bold : pw.FontWeight.normal,
      );

  pw.Widget table(ReportTable b) {
    final size = b.headers.length >= 8 ? 7.5 : 8.5;
    return pw.Padding(
      padding: const pw.EdgeInsets.only(top: 6),
      child: pw.Table(
        columnWidths: b.weights == null ? null : {for (var i = 0; i < b.weights!.length; i++) i: pw.FlexColumnWidth(b.weights![i])},
        border: const pw.TableBorder(
          horizontalInside: pw.BorderSide(color: ReportColors.rule, width: 0.3),
          bottom: pw.BorderSide(color: ReportColors.rule, width: 0.3),
        ),
        children: [
          pw.TableRow(
            decoration: const pw.BoxDecoration(border: pw.Border(bottom: pw.BorderSide(color: ReportColors.rule, width: 0.6))),
            children: [
              for (final h in b.headers)
                pw.Padding(
                  padding: const pw.EdgeInsets.all(3),
                  child: pw.Text(pdfSafe(h), style: style(size: size - 0.5, bold: true, color: ReportColors.muted)),
                ),
            ],
          ),
          for (var r = 0; r < b.rows.length; r++)
            pw.TableRow(children: [
              for (var c = 0; c < b.rows[r].length; c++)
                pw.Padding(
                  padding: const pw.EdgeInsets.all(3),
                  child: pw.Text(
                    pdfSafe(b.rows[r][c]),
                    style: b.statuses[c] != null
                        ? style(size: size, bold: true, color: ReportColors.forStatus(b.statuses[c]![r]))
                        : style(size: size),
                  ),
                ),
            ]),
        ],
      ),
    );
  }

  pw.Widget columns(ReportColumns b) {
    const height = 90.0;
    final maxTotal = b.bars.fold<int>(1, (m, x) => math.max(m, x.$2 + x.$3));
    final labelEvery = b.bars.length <= 14 ? 1 : (b.bars.length / 8).ceil();

    return pw.Padding(
      padding: const pw.EdgeInsets.only(top: 8),
      child: pw.Column(crossAxisAlignment: pw.CrossAxisAlignment.start, children: [
        pw.Row(children: [
          pw.Text(pdfSafe(b.title), style: style(size: 9, bold: true)),
          pw.SizedBox(width: 10),
          pw.Container(width: 7, height: 7, color: ReportColors.own),
          pw.Text(' ${pdfSafe(b.ownLabel)}   ', style: style(size: 7.5, color: ReportColors.muted)),
          pw.Container(width: 7, height: 7, color: ReportColors.admin),
          pw.Text(' ${pdfSafe(b.adminLabel)}', style: style(size: 7.5, color: ReportColors.muted)),
        ]),
        pw.SizedBox(height: 4),
        pw.SizedBox(
          height: height + 12,
          child: pw.Row(crossAxisAlignment: pw.CrossAxisAlignment.end, children: [
            for (final bar in b.bars)
              pw.Expanded(
                child: pw.Padding(
                  padding: const pw.EdgeInsets.symmetric(horizontal: 1),
                  child: pw.Column(mainAxisAlignment: pw.MainAxisAlignment.end, children: [
                    if (b.bars.length <= 14) pw.Text('${bar.$2 + bar.$3}', style: style(size: 6.5)),
                    if (bar.$3 > 0) pw.Container(height: bar.$3 / maxTotal * height, color: ReportColors.admin),
                    pw.Container(height: math.max(0.5, bar.$2 / maxTotal * height), color: ReportColors.own),
                  ]),
                ),
              ),
          ]),
        ),
        pw.Row(children: [
          for (var i = 0; i < b.bars.length; i++)
            pw.Expanded(
              child: pw.Text(
                i % labelEvery == 0 ? pdfSafe(b.bars[i].$1) : '',
                textAlign: pw.TextAlign.center,
                style: style(size: 6.5, color: ReportColors.muted),
              ),
            ),
        ]),
      ]),
    );
  }

  pw.Widget bars(ReportBars b) {
    final maxValue = b.items.fold<double>(1, (m, x) => math.max(m, x.$2));
    return pw.Padding(
      padding: const pw.EdgeInsets.only(top: 8),
      child: pw.Column(crossAxisAlignment: pw.CrossAxisAlignment.start, children: [
        pw.Text(pdfSafe(b.title), style: style(size: 9, bold: true)),
        pw.SizedBox(height: 3),
        for (final item in b.items)
          pw.Padding(
            padding: const pw.EdgeInsets.symmetric(vertical: 1.5),
            child: pw.Row(children: [
              pw.SizedBox(width: 170, child: pw.Text(pdfSafe(item.$1), textAlign: pw.TextAlign.right, style: style(size: 8))),
              pw.SizedBox(width: 6),
              pw.Container(width: math.max(2, 280 * item.$2 / maxValue), height: 8, color: ReportColors.own),
              pw.SizedBox(width: 4),
              pw.Text('${item.$2}${b.unit}', style: style(size: 8, bold: true)),
            ]),
          ),
      ]),
    );
  }

  pw.Widget line(ReportLine b) {
    final last = b.values.length - 1;
    final all = [...b.values, ?b.target];
    final lo = (all.reduce(math.min) * 0.9).floorToDouble();
    final hi = (all.reduce(math.max) * 1.1).ceilToDouble();
    String fmt(num v) => '${v == v.roundToDouble() ? v.toInt() : v}${b.suffix}';

    return pw.Padding(
      padding: const pw.EdgeInsets.only(top: 8),
      child: pw.Column(crossAxisAlignment: pw.CrossAxisAlignment.start, children: [
        pw.Row(children: [
          pw.Text(pdfSafe(b.title), style: style(size: 9, bold: true)),
          if (b.target != null) pw.Text('   - - ${pdfSafe(b.targetLabel)}', style: style(size: 7.5, color: ReportColors.green)),
        ]),
        pw.SizedBox(
          height: 110,
          child: pw.Chart(
            grid: pw.CartesianGrid(
              xAxis: pw.FixedAxis<double>(
                [0, last / 2, last.toDouble()],
                format: (v) => pdfSafe(b.labels[v.round().clamp(0, last)]),
                textStyle: style(size: 6.5, color: ReportColors.muted),
              ),
              yAxis: pw.FixedAxis<double>(
                [lo, (lo + hi) / 2, hi],
                format: (v) => fmt(v),
                divisions: true,
                textStyle: style(size: 6.5, color: ReportColors.muted),
              ),
            ),
            datasets: [
              pw.LineDataSet<pw.PointChartValue>(
                data: [for (var i = 0; i <= last; i++) pw.PointChartValue(i.toDouble(), b.values[i])],
                color: ReportColors.own,
                drawPoints: b.values.length <= 14,
              ),
              if (b.target != null)
                pw.LineDataSet<pw.PointChartValue>(
                  data: [pw.PointChartValue(0, b.target!), pw.PointChartValue(last.toDouble(), b.target!)],
                  color: ReportColors.green,
                  lineWidth: 1,
                  drawPoints: false,
                ),
            ],
          ),
        ),
      ]),
    );
  }

  pw.Widget block(ReportBlock b) => switch (b) {
        ReportText() => pw.Padding(
            padding: const pw.EdgeInsets.only(top: 4),
            child: pw.Text(pdfSafe(b.text), style: style(color: b.muted ? ReportColors.muted : ReportColors.ink, bold: b.bold)),
          ),
        ReportBullets() => pw.Padding(
            padding: const pw.EdgeInsets.only(top: 3, left: 8),
            child: pw.Column(
              crossAxisAlignment: pw.CrossAxisAlignment.start,
              children: [for (final item in b.items) pw.Padding(padding: const pw.EdgeInsets.only(top: 2), child: pw.Text('- ${pdfSafe(item)}', style: style()))],
            ),
          ),
        ReportStats() => pw.Padding(
            padding: const pw.EdgeInsets.only(top: 6),
            child: pw.Row(children: [
              for (final (value, label) in b.items)
                pw.Expanded(
                  child: pw.Container(
                    margin: const pw.EdgeInsets.only(right: 6),
                    padding: const pw.EdgeInsets.all(8),
                    decoration: pw.BoxDecoration(border: pw.Border.all(color: ReportColors.rule, width: 0.5)),
                    child: pw.Column(crossAxisAlignment: pw.CrossAxisAlignment.start, children: [
                      pw.Text(pdfSafe(value), style: style(size: 18, bold: true, color: ReportColors.accent)),
                      pw.Text(pdfSafe(label), style: style(size: 8, color: ReportColors.muted)),
                    ]),
                  ),
                ),
            ]),
          ),
        ReportTable() => table(b),
        ReportColumns() => columns(b),
        ReportBars() => bars(b),
        ReportLine() => line(b),
      };

  doc.addPage(pw.MultiPage(
    pageFormat: PdfPageFormat.a4,
    margin: const pw.EdgeInsets.all(40),
    build: (context) => [
      pw.Text(pdfSafe(report.title), style: style(size: 22, bold: true, color: ReportColors.accent)),
      pw.Text(pdfSafe(report.subtitle), style: style(color: ReportColors.muted)),
      for (final section in report.sections) ...[
        pw.SizedBox(height: 14),
        pw.Text(pdfSafe(section.title), style: style(size: 14, bold: true, color: ReportColors.accent)),
        pw.Divider(color: ReportColors.accent, thickness: 1),
        for (final b in section.blocks) block(b),
      ],
    ],
    footer: (context) => pw.Align(
      alignment: pw.Alignment.centerRight,
      child: pw.Text('Page ${context.pageNumber} of ${context.pagesCount}', style: style(size: 8, color: ReportColors.muted)),
    ),
  ));

  return doc.save();
}

/// Replaces the characters Helvetica cannot draw. Everything else Latin-1 passes through.
String pdfSafe(String text) {
  final out = StringBuffer();
  for (final rune in text.runes) {
    switch (rune) {
      case 0x2192: // →
        out.write('->');
      case 0x2026: // …
        out.write('...');
      case 0x2013 || 0x2014: // – —
        out.write('-');
      case 0x2018 || 0x2019: // ‘ ’
        out.write("'");
      case 0x201C || 0x201D: // “ ”
        out.write('"');
      case 0x2264: // ≤
        out.write('<=');
      case 0x2265: // ≥
        out.write('>=');
      case 0x25B2: // ▲
        out.write('+');
      case 0x25BC: // ▼
        out.write('-');
      default:
        out.writeCharCode(rune <= 0xFF ? rune : 0x3F); // ? for anything else
    }
  }
  return out.toString();
}
