import 'dart:convert';

import 'diagnostics_category.dart';
import 'diagnostics_log.dart';

/// Recorded runs as CSV, one row per line of output.
///
/// A run with no lines — a vulnerability assessment reports one summary and nothing else — still
/// gets one row, with an empty `line`, so it appears in the export rather than vanishing from it.
/// The summary is repeated on each row so a row read on its own, in a spreadsheet filtered down to
/// the failures, still says which run it belongs to.
String diagnosticsToCsv(List<DiagnosticsEntry> entries) {
  final rows = <List<String>>[
    ['run', 'recorded_at', 'kind', 'source', 'summary', 'outcome', 'line'],
    for (final entry in entries)
      if (entry.lines.isEmpty)
        _row(entry, '', '')
      else
        for (final line in entry.lines)
          _row(entry, line.category == null ? '' : diagnosticsCategoryLabel(line.category!), line.text),
  ];
  // CRLF, as RFC 4180 has it, and what spreadsheet imports expect.
  return '${rows.map((row) => row.map(_cell).join(',')).join('\r\n')}\r\n';
}

List<String> _row(DiagnosticsEntry entry, String outcome, String line) => [
      entry.title,
      entry.recordedAt.toUtc().toIso8601String(),
      entry.kind.name,
      entry.source ?? '',
      entry.summary ?? '',
      outcome,
      line,
    ];

/// Quoted always rather than only when needed: a note names an application, and an application's
/// name can hold a comma, a quote or a bracket — "Microsoft Visual C++ 2015-2022 Redistributable
/// (x64)" is one — so deciding per cell would be one more place to get it wrong.
String _cell(String value) => '"${value.replaceAll('"', '""')}"';

/// Recorded runs as JSON, with the route each line is about where there is one.
String diagnosticsToJson(List<DiagnosticsEntry> entries) => const JsonEncoder.withIndent('  ').convert([
      for (final entry in entries)
        {
          'run': entry.title,
          'recordedAt': entry.recordedAt.toUtc().toIso8601String(),
          'kind': entry.kind.name,
          'source': entry.source,
          'summary': entry.summary,
          'lines': [
            for (final line in entry.lines)
              {
                'text': line.text,
                if (line.category != null) 'outcome': line.category,
                if (line.target != null) 'target': line.target,
              },
          ],
        },
    ]);

/// A run as plain text, for the clipboard: a header, the summary and one line per item.
String diagnosticsToText(List<DiagnosticsEntry> entries) => entries
    .map((entry) => [
          '${entry.title} — ${_stamp(entry.recordedAt)}${entry.source == null ? '' : ' (${entry.source})'}',
          if (entry.summary != null) entry.summary!,
          ...entry.lines.map(
            (line) => line.category == null ? line.text : '[${diagnosticsCategoryLabel(line.category!)}] ${line.text}',
          ),
        ].join('\n'))
    .join('\n\n');

String _stamp(DateTime time) {
  final t = time.toLocal();
  String two(int n) => n.toString().padLeft(2, '0');
  return '${t.year}-${two(t.month)}-${two(t.day)} ${two(t.hour)}:${two(t.minute)}:${two(t.second)}';
}
