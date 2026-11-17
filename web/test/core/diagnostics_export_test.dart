import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/diagnostics/diagnostics_export.dart';
import 'package:kintsugi_web/core/diagnostics/diagnostics_log.dart';

DiagnosticsEntry _entry({
  String title = 'Check for Updates',
  DiagnosticsKind kind = DiagnosticsKind.error,
  String? summary = '1 failed.',
  List<DiagnosticsLine> lines = const [],
}) =>
    DiagnosticsEntry(
      id: 1,
      title: title,
      kind: kind,
      recordedAt: DateTime.utc(2026, 11, 4, 10, 12, 41),
      summary: summary,
      source: 'Applications',
      lines: lines,
    );

/// What leaves the app in an export: one row per line, with nothing a spreadsheet would misread.
void main() {
  group('CSV', () {
    test('has a header and one row per line, with the run repeated on each', () {
      final csv = diagnosticsToCsv([
        _entry(lines: const [DiagnosticsLine('Slack (macOS): no version.'), DiagnosticsLine('Zoom (macOS): no script.')]),
      ]);

      final rows = const LineSplitter().convert(csv);
      expect(rows[0], '"run","recorded_at","kind","source","summary","outcome","line"');
      expect(rows, hasLength(3));
      expect(rows[1], '"Check for Updates","2026-11-04T10:12:41.000Z","error","Applications","1 failed.","","Slack (macOS): no version."');
    });

    test('gives a run with no lines one row, so it is not missing from the export', () {
      final csv = diagnosticsToCsv([_entry(kind: DiagnosticsKind.success, summary: 'Checked 412.')]);

      final rows = const LineSplitter().convert(csv);
      expect(rows, hasLength(2));
      expect(rows[1], endsWith(',"Checked 412.","",""'));
    });

    test('quotes every cell and doubles a quote inside one', () {
      final csv = diagnosticsToCsv([
        _entry(lines: const [DiagnosticsLine('Microsoft Visual C++ 2015-2022 Redistributable (x64) (Windows): says "no", twice, here')]),
      ]);

      expect(csv, contains('"Microsoft Visual C++ 2015-2022 Redistributable (x64) (Windows): says ""no"", twice, here"'));
    });

    test('writes what each row concluded in its own column, named as the tab is', () {
      final csv = diagnosticsToCsv([
        _entry(lines: const [
          DiagnosticsLine('Slack (macOS): no version.', category: 'failed'),
          DiagnosticsLine('ncstreamer (macOS): none.', category: 'noKnownPath'),
          DiagnosticsLine('Firefox (macOS)', category: 'resolved', quiet: true),
          DiagnosticsLine('...and 3 more not listed.'),
        ]),
      ]);

      final rows = const LineSplitter().convert(csv);
      expect(rows[1], contains('"Failed","Slack (macOS): no version."'));
      expect(rows[2], contains('"No known path","ncstreamer (macOS): none."'));
      expect(rows[3], contains('"Resolved","Firefox (macOS)"'));
      expect(rows[4], endsWith('"","...and 3 more not listed."'), reason: 'a line with no outcome has an empty cell');
    });

    test('ends rows with CRLF, as spreadsheets expect', () {
      expect(diagnosticsToCsv([_entry()]), contains('\r\n'));
    });

    test('an entry with no summary or source gives empty cells, not the word null', () {
      final csv = diagnosticsToCsv([
        DiagnosticsEntry(id: 1, title: 't', kind: DiagnosticsKind.info, recordedAt: DateTime.utc(2026), lines: const []),
      ]);

      expect(csv, isNot(contains('null')));
    });
  });

  group('JSON', () {
    test('lists each run with its lines, and the route only where a line has one', () {
      final decoded = jsonDecode(diagnosticsToJson([
        _entry(lines: const [DiagnosticsLine('a', target: '/applications?name=a'), DiagnosticsLine('b')]),
      ])) as List<dynamic>;

      final run = decoded.single as Map<String, dynamic>;
      expect(run['run'], 'Check for Updates');
      expect(run['kind'], 'error');
      expect(run['recordedAt'], '2026-11-04T10:12:41.000Z');
      expect(run['source'], 'Applications');
      final lines = run['lines'] as List<dynamic>;
      expect(lines[0], {'text': 'a', 'target': '/applications?name=a'});
      expect(lines[1], {'text': 'b'});
    });
  });

  group('JSON outcomes', () {
    test('carries the outcome key on a line that has one, and omits it on one that does not', () {
      final decoded = jsonDecode(diagnosticsToJson([
        _entry(lines: const [
          DiagnosticsLine('a', category: 'failed'),
          DiagnosticsLine('b'),
        ]),
      ])) as List<dynamic>;

      final lines = (decoded.single as Map<String, dynamic>)['lines'] as List<dynamic>;
      expect(lines[0], {'text': 'a', 'outcome': 'failed'});
      expect(lines[1], {'text': 'b'});
    });
  });

  group('plain text', () {
    test('marks a line with the outcome it was filed under', () {
      final text = diagnosticsToText([
        _entry(lines: const [DiagnosticsLine('Slack (macOS): no version.', category: 'failed'), DiagnosticsLine('plain')]),
      ]);

      expect(text, contains('[Failed] Slack (macOS): no version.'));
      expect(text, contains('\nplain'));
    });

    test('has a header, the summary and one line per item, with runs separated by a blank line', () {
      final text = diagnosticsToText([
        _entry(lines: const [DiagnosticsLine('Slack (macOS): no version.')]),
        _entry(title: 'Second', summary: null),
      ]);

      final parts = text.split('\n\n');
      expect(parts, hasLength(2));
      expect(parts[0].split('\n')[1], '1 failed.');
      expect(parts[0], contains('Slack (macOS): no version.'));
      expect(parts[0].split('\n').first, startsWith('Check for Updates — '));
      expect(parts[0].split('\n').first, endsWith('(Applications)'));
    });
  });
}
